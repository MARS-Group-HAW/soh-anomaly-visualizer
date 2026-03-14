using Godot;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;



public partial class PlaybackVisualizer : Node3D
{


	
	private Dictionary<string, VisualAgent> _activeAgents = new();
	private List<(Vector3 Pos, string Name)> _stallData = new();
	
	[Export] public PackedScene MaleModel { get; set; }
	[Export] public PackedScene FemaleModel { get; set; }

	// Stand Models
	[Export] public PackedScene AtmModel { get; set; }
	[Export] public PackedScene FirebarrelModel { get; set; }
	[Export] public PackedScene FoodstallModel { get; set; }
	[Export] public PackedScene MarketstallModel { get; set; }
	[Export] public PackedScene ToiletModel { get; set; }
	[Export] public PackedScene WinestallModel { get; set; }

	// Y-Height of stalls in inspector (so they don't fly or sink into the ground!)
	[Export(PropertyHint.Range, "-5.0, 5.0, 0.05")] public float AtmOffsetY { get; set; } = 0.0f;
	[Export(PropertyHint.Range, "-5.0, 5.0, 0.05")] public float FirebarrelOffsetY { get; set; } = 0.5f;
	[Export(PropertyHint.Range, "-5.0, 5.0, 0.05")] public float FoodstallOffsetY { get; set; } = 0.0f;
	[Export(PropertyHint.Range, "-5.0, 5.0, 0.05")] public float MarketstallOffsetY { get; set; } = 0.0f;
	[Export(PropertyHint.Range, "-5.0, 5.0, 0.05")] public float ToiletOffsetY { get; set; } = 0.0f;
	[Export(PropertyHint.Range, "-5.0, 5.0, 0.05")] public float WinestallOffsetY { get; set; } = 0.0f;

	// Scale (size) of stalls in inspector
	[Export(PropertyHint.Range, "0.1, 10.0, 0.1")] public float AtmScale { get; set; } = 1.5f;
	[Export(PropertyHint.Range, "0.1, 10.0, 0.1")] public float FirebarrelScale { get; set; } = 1.0f;
	[Export(PropertyHint.Range, "0.1, 10.0, 0.1")] public float FoodstallScale { get; set; } = 1.5f;
	[Export(PropertyHint.Range, "0.1, 10.0, 0.1")] public float MarketstallScale { get; set; } = 1.5f;
	[Export(PropertyHint.Range, "0.1, 10.0, 0.1")] public float ToiletScale { get; set; } = 1.5f;
	[Export(PropertyHint.Range, "0.1, 10.0, 0.1")] public float WinestallScale { get; set; } = 1.5f;

	private MultiMeshInstance3D _maleMultiMesh;
	private MultiMeshInstance3D _femaleMultiMesh;
	
	// We remember the original GLB rotation offset (often models lie on their bellies otherwise!)
	private Transform3D _maleOffset = Transform3D.Identity;
	private Transform3D _femaleOffset = Transform3D.Identity;

	private long _currentTick = 0;
	private long _maxTick = 0;
	private double _timer = 0;
	private bool _isPlaying = false;
	private float _speedMultiplier = 1.0f;
	private float _baseSecondsPerTick = 0.05f; 
	
	private const int MapZoom = 19;
	private double _centerPixelX = 0;
	private double _centerPixelY = 0;

	// One local Godot unit = One meter. Calculated in start!
	private float WorldScale = 1.0f;

	private Label _infoLabel;
	private HSlider _timeSlider;
	private Button _playPauseBtn;
	
	// UI elements
	private Button _legendToggleBtn;
	private PanelContainer _legendPanel;
	private Button _mapToggleBtn;
	
	// Node to hold and toggle the 2D ground map tiles
	private Node3D _groundTilesNode;

	// Interactive Selection UI
	private PanelContainer _tooltipPanel;
	private Label _tooltipLabel;
	private string _selectedAgentId = null;
	private int _selectedStallIndex = -1;

	public override async void _Ready()
	{
		// DIAGNOSTIC: Set to Red for a moment to see if script is even running!
		RenderingServer.SetDefaultClearColor(Colors.Red);
		
		// 0. NIGHT MODE: Dark, bluish Ambient Light
		var env = new Godot.Environment();
		env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
		// Extremely dark blue, so that alleys are really dark
		env.AmbientLightColor = new Color(0.05f, 0.08f, 0.12f); 
		env.AmbientLightEnergy = 0.05f; // Very important: Forces Godot to lower the brightness
		env.TonemapMode = Godot.Environment.ToneMapper.Aces; // More realistic exposure curve
		
		// Dark blue, starry night sky!
		env.BackgroundMode = Godot.Environment.BGMode.Color;
		env.BackgroundColor = new Color(0.02f, 0.05f, 0.12f); // Almost black, but with a cold shade of blue
		
		// Set default clear color for the viewport just in case
		RenderingServer.SetDefaultClearColor(new Color(0.02f, 0.05f, 0.12f));
		
		// Enable glow (helps extremely later on, when you want lamps/fires at stalls to light up)
		env.GlowEnabled = true;
		env.GlowIntensity = 1.0f;
		env.GlowBloom = 0.5f;

		var we = new WorldEnvironment();
		we.Environment = env;
		AddChild(we);

		// "Moonlight": Very soft, extremely subtle blue fill light from slightly diagonal above
		var moonlight = new DirectionalLight3D();
		moonlight.RotationDegrees = new Vector3(-45, 135, 0); 
		moonlight.LightColor = new Color(0.7f, 0.8f, 1.0f); // Cold, pale light
		moonlight.LightEnergy = 0.2f; // Extremely heavily reduced, was 0.3 before
		moonlight.ShadowEnabled = true; // Enable shadows for more dramatic night visuals
		AddChild(moonlight);
		
		// Cold, barely visible bounce light from the other side
		var fillLight = new DirectionalLight3D();
		fillLight.RotationDegrees = new Vector3(-30, -45, 0); 
		fillLight.LightColor = new Color(0.5f, 0.6f, 0.8f);
		fillLight.LightEnergy = 0.02f; // Almost off, just so agents aren't pitch black
		fillLight.ShadowEnabled = false; 
		AddChild(fillLight);

		// 0.5 Delete the giant Godot player sun in Main.tscn!
		var defaultSun = GetNodeOrNull<DirectionalLight3D>("DirectionalLight3D");
		if (defaultSun != null)
		{
			defaultSun.Visible = false;
			defaultSun.QueueFree();
		}

		// 1. Initialize the agent particle systems (MultiMesh)
		// Both get EXACTLY the same color now, so there are no dark/bright differences anymore!
		// New color: Blue, with a very slight glow in the night
		_maleMultiMesh = SetupMultiMesh(Colors.DodgerBlue, MaleModel, out _maleOffset);
		AddChild(_maleMultiMesh);
		
		_femaleMultiMesh = SetupMultiMesh(Colors.DodgerBlue, FemaleModel, out _femaleOffset);
		AddChild(_femaleMultiMesh);

		// 2. Search for UI nodes
		var hbox = GetNode<HBoxContainer>("CanvasLayer/HUD/MarginContainer/PanelContainer/MarginContainer2/VBoxContainer/HBoxContainer");
		_infoLabel = GetNode<Label>("CanvasLayer/HUD/MarginContainer/PanelContainer/MarginContainer2/VBoxContainer/InfoLabel");
		
		_playPauseBtn = hbox.GetNode<Button>("PlayPauseBtn");
		var backBtn = hbox.GetNode<Button>("BackBtn");
		_timeSlider = hbox.GetNode<HSlider>("TimeSlider");
		var fwdBtn = hbox.GetNode<Button>("FwdBtn");
		var speedSlider = hbox.GetNode<HSlider>("SpeedSlider");

		// Turn off focus for clean buttons
		_playPauseBtn.FocusMode = Control.FocusModeEnum.None;
		backBtn.FocusMode = Control.FocusModeEnum.None;
		_timeSlider.FocusMode = Control.FocusModeEnum.None;
		fwdBtn.FocusMode = Control.FocusModeEnum.None;
		speedSlider.FocusMode = Control.FocusModeEnum.None;

		// Legend UI
		_legendToggleBtn = GetNode<Button>("CanvasLayer/HUD/LegendMargin/VBoxContainer/LegendToggleBtn");
		_legendPanel = GetNode<PanelContainer>("CanvasLayer/HUD/LegendMargin/VBoxContainer/LegendPanel");
		_legendToggleBtn.FocusMode = Control.FocusModeEnum.None;
		
		_legendToggleBtn.Pressed += () => {
			_legendPanel.Visible = !_legendPanel.Visible;
			_legendToggleBtn.Text = _legendPanel.Visible ? "Hide legend ▴" : "Show legend ▾";
		};
		
		// Map Toggle Button (Now for 2D Ground Tiles)
		_mapToggleBtn = GetNode<Button>("CanvasLayer/HUD/LegendMargin/VBoxContainer/MapToggleBtn");
		_mapToggleBtn.FocusMode = Control.FocusModeEnum.None;
		_mapToggleBtn.Pressed += () => {
			if (_groundTilesNode != null)
			{
				_groundTilesNode.Visible = !_groundTilesNode.Visible;
				_mapToggleBtn.Text = _groundTilesNode.Visible ? "🗺️ Disable 2D Ground Map" : "🗺️ Enable 2D Ground Map";
			}
		};

		// Tooltip UI Setup
		_tooltipPanel = new PanelContainer();
		_tooltipPanel.Visible = false;

		// Add a nice semi-transparent dark background for readability
		var styleBox = new StyleBoxFlat();
		styleBox.BgColor = new Color(0.1f, 0.1f, 0.1f, 0.85f);
		styleBox.SetCornerRadiusAll(4);
		styleBox.ContentMarginBottom = 6;
		styleBox.ContentMarginTop = 6;
		styleBox.ContentMarginLeft = 8;
		styleBox.ContentMarginRight = 8;
		_tooltipPanel.AddThemeStyleboxOverride("panel", styleBox);

		_tooltipLabel = new Label();
		_tooltipLabel.AddThemeColorOverride("font_color", Colors.White);
		_tooltipPanel.AddChild(_tooltipLabel);
		
		// Add to CanvasLayer so it stays on top of everything
		GetNode("CanvasLayer").AddChild(_tooltipPanel);

		_playPauseBtn.Text = _isPlaying ? "❚❚" : "▶";
		_playPauseBtn.Pressed += () => {
			_isPlaying = !_isPlaying;
			_playPauseBtn.Text = _isPlaying ? "❚❚" : "▶";
		};

		backBtn.Pressed += () => {
			_currentTick = Math.Max(0, _currentTick - 10);
			if (_timeSlider != null) _timeSlider.SetValueNoSignal(_currentTick);
			LoadTick(_currentTick);
		};

		_timeSlider.ValueChanged += (value) => 
		{
			_currentTick = (long)value;
			LoadTick(_currentTick);
		};

		fwdBtn.Pressed += () => {
			_currentTick = Math.Min(_maxTick, _currentTick + 10);
			if (_timeSlider != null) _timeSlider.SetValueNoSignal(_currentTick);
			LoadTick(_currentTick);
		};

		speedSlider.ValueChanged += (value) => 
		{
			_speedMultiplier = (float)value;
			UpdateInfoLabel();
		};

		// 3. Database connection
		try
		{
			_connection = new NpgsqlConnection(connString);
			_connection.Open();
			GD.Print("✅ Successfully connected to MARS PostgreSQL!");

			// 3.1 Performance Fix: If the table contains 14,000+ Ticks, Godot dies on Selects without index. 
			// We check/create a clean tick index here once on server start!
			GD.Print("Checking/Creating database indices for speed up...");
			using (var idxCmd = new NpgsqlCommand("CREATE INDEX IF NOT EXISTS idx_trvlr_tick ON rathausmarkt_2024.desiremarkettraveler (tick);", _connection))
			{
				idxCmd.ExecuteNonQuery();
			}
			GD.Print("✅ Database indices are ready. Queries are fast now.");

			using var cmd = new NpgsqlCommand("SELECT MAX(tick) FROM rathausmarkt_2024.desiremarkettraveler;", _connection);
			var result = cmd.ExecuteScalar();
			if (result != DBNull.Value)
			{
				_maxTick = Convert.ToInt64(result);
				GD.Print($"Maximum tick from Rathausmarkt in DB: {_maxTick}");
				if (_timeSlider != null) _timeSlider.MaxValue = _maxTick;
			}

			using var cmdBnd = new NpgsqlCommand("SELECT MIN(x), MAX(x), MIN(y), MAX(y) FROM rathausmarkt_2024.desiremarkettraveler;", _connection);
			using var reader = cmdBnd.ExecuteReader();
			if (reader.Read())
			{
				double minLon = reader.IsDBNull(0) ? 0 : reader.GetDouble(0);
				double maxLon = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);
				double minLat = reader.IsDBNull(2) ? 0 : reader.GetDouble(2);
				double maxLat = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);
				
				reader.Close();
				double centerLon = (minLon + maxLon) / 2.0;
				double centerLat = (minLat + maxLat) / 2.0;

				// Fallback for Hamburg if DB is empty
				if (Math.Abs(centerLon) < 0.001 || Math.Abs(centerLat) < 0.001)
				{
					centerLon = 9.9936;
					centerLat = 53.5511;
					GD.Print("⚠️ Database bounds empty or 0! Falling back to Hamburg center.");
				}

				// Calculate real Godot scale by computing meters per pixel on the longitude
				// Earth: 40075016 m circumference. At MapZoom 18 there are 256 * 2^18 pixels on the equator.
				double centerLatRad = centerLat * Math.PI / 180.0;
				double metersPerPixel = 40075016.686 * Math.Cos(centerLatRad) / (256.0 * Math.Pow(2.0, MapZoom));
				
				WorldScale = (float)metersPerPixel; // ca. 0.355
				GD.Print($"🌍 Godot WorldScale calibrated to: {WorldScale} (1 Unit = 1 Real Meter on the square!)");

				CalculateGlobalPixelVector(centerLon, centerLat, MapZoom, out _centerPixelX, out _centerPixelY);
				
				// 4. Build stalls from DB and as 3D objects
				using var stallCmd = new NpgsqlCommand("SELECT \"MarketStallsJson\" FROM rathausmarkt_2024.marketanalyticsagent LIMIT 1;", _connection);
				var jsonResult = stallCmd.ExecuteScalar()?.ToString();
				if (!string.IsNullOrEmpty(jsonResult))
				{
					try 
					{
						var stalls = JsonSerializer.Deserialize<List<MarketStall>>(jsonResult);
						if (stalls != null)
						{
							var stallMat = new StandardMaterial3D() { AlbedoColor = new Color(0.2f, 0.4f, 1.0f) };
							
							foreach (var stall in stalls)
							{
								Vector3 pos3D = LonLatToLocalPos3D(stall.X, stall.Y, MapZoom);
								
								Node3D stallNode = null;
								string nameLower = stall.Name.ToLower();
								float yOffset = 0f;
								float scaleFactor = 1.5f;

								if (nameLower.Contains("geldautomat") && AtmModel != null) { stallNode = AtmModel.Instantiate<Node3D>(); yOffset = AtmOffsetY; scaleFactor = AtmScale; }
								else if (nameLower.Contains("feuertonne") && FirebarrelModel != null) { stallNode = FirebarrelModel.Instantiate<Node3D>(); yOffset = FirebarrelOffsetY; scaleFactor = FirebarrelScale; }
								else if (nameLower.Contains("gastronomie") && FoodstallModel != null) { stallNode = FoodstallModel.Instantiate<Node3D>(); yOffset = FoodstallOffsetY; scaleFactor = FoodstallScale; }
								else if (nameLower.Contains("glühwein") && WinestallModel != null) { stallNode = WinestallModel.Instantiate<Node3D>(); yOffset = WinestallOffsetY; scaleFactor = WinestallScale; }
								else if (nameLower.Contains("toilette") && ToiletModel != null) { stallNode = ToiletModel.Instantiate<Node3D>(); yOffset = ToiletOffsetY; scaleFactor = ToiletScale; }
								else if (nameLower.Contains("verkauf") && MarketstallModel != null) { stallNode = MarketstallModel.Instantiate<Node3D>(); yOffset = MarketstallOffsetY; scaleFactor = MarketstallScale; }

								if (stallNode != null)
								{
									stallNode.Scale = new Vector3(scaleFactor, scaleFactor, scaleFactor);
									stallNode.Position = pos3D + new Vector3(0, yOffset, 0); 
									
									// --- ADD LIGHTING ---
									// We add a dedicated warm light directly under the roof of every market stall!
									// Exceptions: Toilets and ATMs get cold/no bright light.
									if (!nameLower.Contains("toilette") && !nameLower.Contains("geldautomat"))
									{
										if (nameLower.Contains("feuertonne")) {
											var stallLight = new OmniLight3D();
											stallLight.LightColor = new Color(1.0f, 0.6f, 0.2f); // Intense fire orange
											stallLight.LightEnergy = 2.0f;
											stallLight.OmniRange = 8.0f;
											stallLight.Position = new Vector3(0, 1.2f, 0); // Light comes right out of the barrel
											stallLight.ShadowEnabled = true; 
											stallNode.AddChild(stallLight);
										} 
										else if (nameLower.Contains("verkauf")) {
											var stallLight = new OmniLight3D();
											stallLight.LightColor = new Color(1.0f, 0.85f, 0.6f); 
											stallLight.LightEnergy = 1.0f;
											stallLight.OmniRange = 10.0f; 
											// Sales stands: Push light slightly forward (-Z is often front in Blender/Godot),
											// so the inner counter does not cast a black shadow to the front!
											stallLight.Position = new Vector3(0, 0.9f, 1.2f); 
											stallLight.ShadowEnabled = true;
											stallNode.AddChild(stallLight);
										}
										else {
											// Gastronomy, mulled wine etc. with very high roof
											var stallLight = new OmniLight3D();
											stallLight.LightColor = new Color(1.0f, 0.85f, 0.6f); // Warm lightbulb light
											stallLight.LightEnergy = 1.0f;
											stallLight.OmniRange = 10.0f;
											stallLight.Position = new Vector3(0, 1.4f, 1.2f); // Push forward here too
											stallLight.ShadowEnabled = true; 
											stallNode.AddChild(stallLight);
										}
									}
									else if (nameLower.Contains("geldautomat"))
									{
										var atmLight = new OmniLight3D();
										atmLight.LightColor = new Color(0.6f, 0.8f, 1.0f); // Cold, technical blue/white
										atmLight.LightEnergy = 1.5f;
										atmLight.OmniRange = 4.0f;
										atmLight.Position = new Vector3(0.8f, 1.5f, 0.2f); // At the top of the display
										atmLight.ShadowEnabled = false; 
										stallNode.AddChild(atmLight);
									}
									
									AddChild(stallNode);
								}
								else 
								{
									// Fallback to cube, in case no model was assigned in the editor
									var meshInstance = new MeshInstance3D();
									var box = new BoxMesh() { Size = new Vector3(2.5f, 2.0f, 2.5f) };
									box.Material = stallMat;
									meshInstance.Mesh = box;
									meshInstance.Position = pos3D + new Vector3(0, 1.0f, 0); 
									stallNode = meshInstance;
									AddChild(stallNode);
								}

									// Instead of a static 3D sign, we remember the stall for clicks
									_stallData.Add((stallNode.Position, stall.Name));
								}
						}
					}
					catch (Exception e)
					{
						GD.PrintErr("Error building stalls in 3D: " + e.Message);
					}
				}

				// 5. Load OSM Map 3D tiles
				await DownloadAndSetupMapTiles(minLon, maxLon, minLat, maxLat);
				
				// 6. Load the city parts (split for performance) and calibrate exactly!
				try 
				{
					string[] cityParts = {
						"res://Assets/rathaus_streets.glb",
						"res://Assets/rathaus_building.glb",
						"res://Assets/rathaus_background_buildings.glb",
						"res://Assets/rathaus_outdoor_area.glb"
					};

					Vector3 osmOrigin = LonLatToLocalPos3D(9.9915, 53.5505, MapZoom);

					foreach (var partPath in cityParts)
					{
						if (!FileAccess.FileExists(partPath)) continue;

						var scene = GD.Load<PackedScene>(partPath);
						if (scene == null) continue;

						var model = scene.Instantiate() as Node3D;
						model.Scale = new Vector3(1, 1, 1);
						model.Position = new Vector3(osmOrigin.X, 0, osmOrigin.Z);
						
						// Basic cleaning for all parts
						RemoveAllLightsRec(model);
						DarkenMaterialsRec(model);
						
						// Only search for lamps in the outdoor area to save performance
						if (partPath.Contains("outdoor_area"))
						{
							AddLightsToStreetLampsRec(model);
						}
						
						AddChild(model);
						GD.Print($"✅ City part loaded: {partPath}");
					}
					GD.Print($"✅ All rathausmarkt environment parts aligned to origin {osmOrigin}!");
				}
				catch (Exception e)
				{
					GD.PrintErr("Error loading split city models: " + e.Message);
				}
			}

			LoadTick(_currentTick);
		}
		catch (Exception e)
		{
			GD.PrintErr($"❌ Database connection error: {e.Message}");
		}
	}





	public override void _Process(double delta)
	{
		// WASD Camera Control
		var camera = GetNodeOrNull<Camera3D>("Camera3D");
		if (camera != null)
		{
			Vector3 moveDir = Vector3.Zero;
			if (Input.IsKeyPressed(Key.W)) moveDir.Z -= 1;
			if (Input.IsKeyPressed(Key.S)) moveDir.Z += 1;
			if (Input.IsKeyPressed(Key.A)) moveDir.X -= 1;
			if (Input.IsKeyPressed(Key.D)) moveDir.X += 1;

			if (moveDir != Vector3.Zero)
			{
				moveDir = moveDir.Normalized();
				
				// Movement based on current camera alignment
				Vector3 forward = -camera.GlobalTransform.Basis.Z;
				Vector3 right = camera.GlobalTransform.Basis.X;
				
				// We don't want to fly into the ground, so we lock Y to the global plane
				forward.Y = 0; 
				right.Y = 0;
				
				// Normalize in case we're looking straight down
				if (forward.LengthSquared() > 0.001f) forward = forward.Normalized();
				if (right.LengthSquared() > 0.001f) right = right.Normalized();

				// The higher we are, the faster we fly!
				float speed = Math.Max(10.0f, camera.Position.Y * 0.8f) * (float)delta; 
				
				// Shift = Sprint
				if (Input.IsKeyPressed(Key.Shift))
				{
					speed *= 3.0f;
				}
				
				camera.GlobalPosition += (forward * -moveDir.Z + right * moveDir.X) * speed;
			}
		}

		AnimateAgents(delta);

		if (_tooltipPanel != null)
		{
			if (_selectedAgentId != null && _activeAgents.TryGetValue(_selectedAgentId, out var agent))
			{
				Vector3 pos3D = agent.CurrentPos + new Vector3(0, 1.8f, 0);
				if (!camera.IsPositionBehind(pos3D))
				{
					_tooltipPanel.Position = camera.UnprojectPosition(pos3D);
					_tooltipLabel.Text = $"Agent ID: {agent.Id}\n\n" +
										 $"Hunger:\t\t\t\t{agent.Hunger:0.0}\n" +
										 $"Thirst:\t\t\t\t{agent.Thirst:0.0}\n" +
										 $"Mood:\t\t{agent.Mood:0.0}%\n" +
										 $"Budget:\t\t\t\t{agent.Budget:0.0} €\n" +
										 $"Exhaustion:\t{agent.Exhaustion:0.0}\n" +
										 $"Bladder:\t\t{agent.Bladder:0.0}";
					_tooltipPanel.Size = Vector2.Zero; // Forces Godot to make the window small again!
					_tooltipPanel.Visible = true;
				}
				else
				{
					_tooltipPanel.Visible = false;
				}
			}
			else if (_selectedStallIndex != -1 && _selectedStallIndex < _stallData.Count)
			{
				var stall = _stallData[_selectedStallIndex];
				Vector3 pos3D = stall.Pos + new Vector3(0, 3.5f, 0);
				if (!camera.IsPositionBehind(pos3D))
				{
					_tooltipPanel.Position = camera.UnprojectPosition(pos3D);
					_tooltipLabel.Text = stall.Name;
					_tooltipPanel.Size = Vector2.Zero; // Forces Godot to make the window small again!
					_tooltipPanel.Visible = true;
				}
				else
				{
					_tooltipPanel.Visible = false;
				}
			}
			else
			{
				_tooltipPanel.Visible = false;
			}
		}

		if (_connection == null || _connection.State != System.Data.ConnectionState.Open) return;
		if (_maxTick == 0 || !_isPlaying) return;

		_timer += delta * _speedMultiplier;
		
		if (_timer >= _baseSecondsPerTick)
		{
			int ticksToAdvance = (int)(_timer / _baseSecondsPerTick);
			_timer -= ticksToAdvance * _baseSecondsPerTick;
			
			_currentTick += ticksToAdvance;
			if (_currentTick > _maxTick) _currentTick = 0; 

			if (_timeSlider != null && _timeSlider.Value != _currentTick) {
				_timeSlider.SetValueNoSignal(_currentTick); 
			}

			LoadTick(_currentTick);
		}
	}





	public override void _ExitTree()
	{
		if (_connection != null && _connection.State == System.Data.ConnectionState.Open)
		{
			_connection.Close();
		}
	}
}
