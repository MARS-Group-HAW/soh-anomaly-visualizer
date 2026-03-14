using Godot;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;



public partial class PlaybackVisualizer : Node3D
{


	private void RemoveAllLightsRec(Node n)
	{
		if (n is Light3D light)
		{
			light.Visible = false;
			light.QueueFree();
		}
		
		foreach (var child in n.GetChildren())
		{
			RemoveAllLightsRec(child);
		}
	}

	private void AddLightsToStreetLampsRec(Node n)
	{
		if (n is MeshInstance3D mi)
		{
			string nameLower = n.Name.ToString().ToLower();

			if (nameLower.Contains("streetlamp") || nameLower.Contains("lamp"))
			{
				// Prevent spawning lights twice, in case the script runs 2x
				bool hasLight = false;
				foreach (var child in n.GetChildren())
				{
					if (child is Light3D) hasLight = true;
				}

				if (!hasLight)
				{
					// Since OmniLights illuminate buildings and create giant light spheres,
					// The user wishes for OmniLights again, we use them now together with the 
					// correct AABB center so they are neatly distributed!
					var streetLight = new OmniLight3D();
					streetLight.LightColor = new Color(1.0f, 0.75f, 0.3f); // Significantly more yellowish/orange (sodium-vapor lamp)
					
					// Moderate strength, since OmniLights radiate in all directions
					streetLight.LightEnergy = 5.0f; 
					streetLight.OmniRange = 15.0f; // Radius of the sphere
					streetLight.ShadowEnabled = true; 
					streetLight.DistanceFadeEnabled = true;
					streetLight.DistanceFadeBegin = 100.0f;
					streetLight.DistanceFadeLength = 20.0f;

					// Many imported OSM models share a single (0,0,0) origin,
					// but the actual geometry (vertices) is located elsewhere.
					// We fetch the physical Bounding Box (AABB) of the lantern mesh!
					var aabb = mi.GetAabb();
					var lampCenter = aabb.GetCenter();
					
					// We position the light at the physical top edge of the lantern 
					// (Center + half height = tip). Then slide down slightly (-0.3f), 
					// so the bulb isn't stuck in the lantern's metal roof!
					streetLight.Position = lampCenter + new Vector3(0, 10.0f, 0); 
					
					n.AddChild(streetLight);
				}
			}
		}

		foreach (var child in n.GetChildren())
		{
			AddLightsToStreetLampsRec(child);
		}
	}

	private void DarkenMaterialsRec(Node n)
	{
		if (n is MeshInstance3D mi && mi.Mesh != null)
		{
			for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
			{
				var matBase = mi.Mesh.SurfaceGetMaterial(i);
				if (matBase == null) matBase = mi.GetActiveMaterial(i);
				
				if (matBase is StandardMaterial3D stdMat)
				{
					// Create copy of material, as instances are often shared
					var newMat = stdMat.Duplicate() as StandardMaterial3D;
					// Turn off emission (glow) completely!
					newMat.EmissionEnabled = false;
					newMat.Emission = new Color(0, 0, 0);
					// Force enable shading
					newMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel;
					
					// Sometimes OSM materials are extremely bright white, we take away 20% brightness:
					newMat.AlbedoColor = newMat.AlbedoColor.Darkened(0.2f);
					
					mi.SetSurfaceOverrideMaterial(i, newMat);
				}
			}
		}
		
		foreach (var child in n.GetChildren())
		{
			DarkenMaterialsRec(child);
		}
	}

	private void CalculateGlobalPixelVector(double lon, double lat, int zoom, out double pixelX, out double pixelY)
	{
		double n = Math.Pow(2.0, zoom);
		pixelX = (lon + 180.0) / 360.0 * n * 256.0;
		
		double latRad = lat * Math.PI / 180.0;
		pixelY = (1.0 - Math.Log(Math.Tan(latRad) + (1.0 / Math.Cos(latRad))) / Math.PI) / 2.0 * n * 256.0;
	}

	// In 3D: X is left/right, Z is forward/backward (the former Y-plane of the map)
	private Vector3 LonLatToLocalPos3D(double lon, double lat, int zoom)
	{
		CalculateGlobalPixelVector(lon, lat, zoom, out double globalX, out double globalY);
		return new Vector3(
			(float)(globalX - _centerPixelX) * WorldScale,
			0.5f, // Agents shouldn't sink halfway into the ground (Radius 0.5)
			(float)(globalY - _centerPixelY) * WorldScale
		);
	}

	private async Task DownloadAndSetupMapTiles(double minLon, double maxLon, double minLat, double maxLat)
	{
		_groundTilesNode = new Node3D();
		_groundTilesNode.Visible = false; // Hide by default
		AddChild(_groundTilesNode);

		CalculateGlobalPixelVector(minLon, maxLat, MapZoom, out double tlX, out double tlY);
		CalculateGlobalPixelVector(maxLon, minLat, MapZoom, out double brX, out double brY);
		
		int minTx = (int)(tlX / 256.0) - 1;
		int maxTx = (int)(brX / 256.0) + 1;
		int minTy = (int)(tlY / 256.0) - 1;
		int maxTy = (int)(brY / 256.0) + 1;

		using var httpClient = new System.Net.Http.HttpClient();
		httpClient.DefaultRequestHeaders.Add("User-Agent", "Godot-SOH-Visualizer/1.0");

		for (int tx = minTx; tx <= maxTx; tx++)
		{
			for (int ty = minTy; ty <= maxTy; ty++)
			{
				string url = $"https://tile.openstreetmap.org/{MapZoom}/{tx}/{ty}.png";
				try 
				{
					byte[] imageBytes = await httpClient.GetByteArrayAsync(url);
					var image = new Image();
					if (image.LoadPngFromBuffer(imageBytes) == Godot.Error.Ok)
					{
						var texture = ImageTexture.CreateFromImage(image);
						
						// Create map tiles as 3D objects, lying flat on the ground (Y-axis)
						var sprite = new Sprite3D();
						sprite.Texture = texture;
						sprite.Axis = Vector3.Axis.Y;
						sprite.PixelSize = WorldScale; // One pixel = 10cm! 
						
						// VERY IMPORTANT: So the satellite images don't glow in the dark, they must be truly Shaded
						sprite.Transparent = false;
						sprite.Shaded = true;
						// So sprites get the environment filter too
						sprite.AlphaCut = SpriteBase3D.AlphaCutMode.OpaquePrepass; 

						double tileGlobalX = tx * 256.0;
						double tileGlobalY = ty * 256.0;

						double spriteLocalX = tileGlobalX + 128.0 - _centerPixelX;
						double spriteLocalY = tileGlobalY + 128.0 - _centerPixelY;
						
						// Y = 0 (Ground)
						sprite.Position = new Vector3((float)spriteLocalX * WorldScale, 0, (float)spriteLocalY * WorldScale);
						
						// Manually turn the white OSM satellite tiles into dark asphalt
						sprite.Modulate = new Color(0.2f, 0.2f, 0.2f);
						
						// Pack under _groundTilesNode instead of directly under AddChild
						_groundTilesNode.AddChild(sprite);
					}
				}
				catch (Exception e)
				{
					GD.PrintErr($"Tile {tx},{ty} missing: {e.Message}");
				}
			}
		}
	}

	private void UpdateInfoLabel()
	{
		if (_infoLabel != null)
		{
			_infoLabel.Text = $"Tick: {_currentTick} / {_maxTick} | Speed: {_speedMultiplier:0.0}x";
		}
	}

	// Here we call the animation loop aside from GUI/Ticks
	private void AnimateAgents(double delta)
	{
		int maleCounter = 0;
		int femaleCounter = 0;
		float lerpFactor = Math.Min(1.0f, 15.0f * (float)delta * _speedMultiplier);

		foreach (var agent in _activeAgents.Values)
		{
			float dist = agent.CurrentPos.DistanceTo(agent.TargetPos);
			
			// Movement step / Interpolation
			float bounceY = 0f;
			float tiltZ = 0f;
			if (dist > 0.001f)
			{
				agent.CurrentPos = agent.CurrentPos.Lerp(agent.TargetPos, lerpFactor);
				agent.WalkCycle += (float)delta * 35.0f * _speedMultiplier; // Tempo-dependent wobble (faster cycle)
				
				// We add an extremely subtle bounce (approx. 18 centimeters)
				bounceY = Mathf.Abs(Mathf.Sin(agent.WalkCycle)) * 0.18f; 
				tiltZ = Mathf.Cos(agent.WalkCycle) * 0.15f; 
			}

			// Determine look direction
			Vector3 lookTarget = agent.TargetPos;
			lookTarget.Y = agent.CurrentPos.Y; // Do not look into the ground
			
			// If moving noticeably, we smoothly rotate into the walking direction
			if (agent.CurrentPos.DistanceSquaredTo(lookTarget) > 0.0001f)
			{
				// In Godot 4, Transform3D.LookingAt is safest to use at the global/local transform level
				Transform3D t = new Transform3D(Basis.Identity, agent.CurrentPos);
				t = t.LookingAt(lookTarget, Vector3.Up);
				
				// We gently Slerp (interpolate) the Godot rotation
				Quaternion currentQuat = agent.CurrentRot.GetRotationQuaternion();
				Quaternion targetQuat = t.Basis.GetRotationQuaternion();
				agent.CurrentRot = new Basis(currentQuat.Slerp(targetQuat, lerpFactor));
			}

			Basis rotBasis = agent.CurrentRot;

			if (dist > 0.001f)
			{
				// Waddle: tilt model sideways
				rotBasis *= new Basis(Vector3.Forward, tiltZ);
			}

			// Models from Blender often look in +Z direction, but Godot's LookingAt aligns -Z to the target.
			// Therefore we turn them 180 degrees around their own axis (Y).
			rotBasis *= new Basis(Vector3.Up, Mathf.Pi);

			// Assemble basis: First Godot alignment, then apply potentially needed GLB rotation!
			Transform3D baseTransform = new Transform3D(rotBasis, agent.CurrentPos + new Vector3(0, bounceY, 0));

			// Write into Godot MultiMesh
			if (agent.IsMale)
			{
				Transform3D finalTransform = baseTransform * _maleOffset;
				if (maleCounter < _maleMultiMesh.Multimesh.InstanceCount)
					_maleMultiMesh.Multimesh.SetInstanceTransform(maleCounter++, finalTransform);
			}
			else
			{
				Transform3D finalTransform = baseTransform * _femaleOffset;
				if (femaleCounter < _femaleMultiMesh.Multimesh.InstanceCount)
					_femaleMultiMesh.Multimesh.SetInstanceTransform(femaleCounter++, finalTransform);
			}
		}
		
		_maleMultiMesh.Multimesh.VisibleInstanceCount = maleCounter;
		_femaleMultiMesh.Multimesh.VisibleInstanceCount = femaleCounter;
	}





	private MultiMeshInstance3D SetupMultiMesh(Color fallbackColor, PackedScene customScene, out Transform3D extractedOffset)
	{
		var mmi = new MultiMeshInstance3D();
		var multiMesh = new MultiMesh();
		multiMesh.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
		multiMesh.InstanceCount = 20000;
		multiMesh.VisibleInstanceCount = 0;
		
		extractedOffset = Transform3D.Identity;
		Mesh extractedMesh = null;
		BaseMaterial3D origMaterial = null;
		
		if (customScene != null)
		{
			var node = customScene.Instantiate() as Node3D;
			if (node != null) 
			{
				extractedMesh = ExtractMeshRec(node, out extractedOffset, out origMaterial);
				// VERY IMPORTANT: Zero out the origin. Blender models are often not exactly centered on (0,0,0), 
				// causing the sub-meshes to float beside / above their actual coordinates and shadows to float mid-air!
				extractedOffset.Origin = Vector3.Zero;
			}
			node?.QueueFree(); // Throw Node out of memory immediately, since we only steal the pure 3D mesh
		}

		if (extractedMesh == null)
		{
			var sphere = new SphereMesh() { Radius = 0.5f, Height = 1.0f };
			var mat = new StandardMaterial3D() { AlbedoColor = fallbackColor, Metallic = 0.2f, Roughness = 0.5f };
			sphere.Material = mat;
			extractedMesh = sphere;
			extractedOffset = Transform3D.Identity;
		}

		multiMesh.Mesh = extractedMesh;
		mmi.Multimesh = multiMesh;
		
		// We FORCE the MultiMesh to assume a defined, clean Godot color.
		// This overwrites gray/broken default materials from downloaded GLBs!
		var solidMat = new StandardMaterial3D();
		solidMat.AlbedoColor = fallbackColor;
		
		// Specular highlights and normal diffuse light make edges visible again!
		solidMat.Roughness = 0.5f; 
		solidMat.Metallic = 0.0f;
		// force ShadingMode to prevent errors
		solidMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel;
		solidMat.DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Burley; 
		
		// Add tiny emission glow so models are never completely black at 0 light
		solidMat.EmissionEnabled = true;
		solidMat.Emission = fallbackColor.Darkened(0.7f); // Very dark blue as self-glow
		solidMat.EmissionEnergyMultiplier = 1.0f; 
		
		mmi.MaterialOverride = solidMat; // Global Override

		// Ensure every sub-mesh in the downloaded model (like clothes/body parts) uses this material too
		if (extractedMesh != null)
		{
			for (int i = 0; i < extractedMesh.GetSurfaceCount(); i++)
			{
				extractedMesh.SurfaceSetMaterial(i, solidMat);
			}
		}
		
		return mmi;
	}

	private Mesh ExtractMeshRec(Node n, out Transform3D transform, out BaseMaterial3D originalMat)
	{
		transform = Transform3D.Identity;
		originalMat = null;
		
		if (n is MeshInstance3D mi && mi.Mesh != null) 
		{
			transform = mi.Transform;
			
			// Extract material to take over original color/texture of the 3D model
			var mat = mi.GetActiveMaterial(0) as BaseMaterial3D;
			if (mat != null) originalMat = mat;
			
			return mi.Mesh;
		}
		
		foreach (var child in n.GetChildren())
		{
			var res = ExtractMeshRec(child, out var childTransform, out originalMat);
			if (res != null)  
			{
				if (n is Node3D n3d)
				{
					transform = n3d.Transform * childTransform;
				}
				else
				{
					transform = childTransform;
				}
				return res;
			}
		}
		return null;
	}

	// --- CAMERA CONTROL IN 3D ---
	private bool _isPanning = false;
	private bool _isRotating = false;

	public override void _UnhandledInput(InputEvent @event)
	{
		var camera = GetNodeOrNull<Camera3D>("Camera3D");
		if (camera == null) return;

		if (@event is InputEventMouseButton mouseBtnEvent)
		{
			if (mouseBtnEvent.ButtonIndex == MouseButton.WheelUp && mouseBtnEvent.Pressed)
			{
				// Zoom in along camera view angle
				camera.Translate(new Vector3(0, 0, -4.0f));
			}
			else if (mouseBtnEvent.ButtonIndex == MouseButton.WheelDown && mouseBtnEvent.Pressed)
			{
				// Zoom out
				camera.Translate(new Vector3(0, 0, 4.0f));
			}

			if (mouseBtnEvent.ButtonIndex == MouseButton.Left)
			{
				_isPanning = mouseBtnEvent.Pressed;

				// On mouse release -> Handle selection
				if (!mouseBtnEvent.Pressed)
				{
					Vector2 clickPos = mouseBtnEvent.Position;

					// 1. Check Stalls
					float minStallDist = float.MaxValue;
					int bestStallIndex = -1;
					for (int i = 0; i < _stallData.Count; i++)
					{
						Vector3 stallPos = _stallData[i].Pos + new Vector3(0, 1.5f, 0);
						if (camera.IsPositionBehind(stallPos)) continue;
						
						Vector2 screenPos = camera.UnprojectPosition(stallPos);
						float dist = screenPos.DistanceTo(clickPos);
						if (dist < 40.0f && dist < minStallDist) {
							minStallDist = dist;
							bestStallIndex = i;
						}
					}

					// 2. Check Agents
					float minAgentDist = float.MaxValue;
					string bestAgentId = null;
					foreach (var kvp in _activeAgents)
					{
						Vector3 agentPos = kvp.Value.CurrentPos + new Vector3(0, 1.0f, 0); // click on torso/head level
						if (camera.IsPositionBehind(agentPos)) continue;
						
						Vector2 screenPos = camera.UnprojectPosition(agentPos);
						float dist = screenPos.DistanceTo(clickPos);
						if (dist < 25.0f && dist < minAgentDist) {
							minAgentDist = dist;
							bestAgentId = kvp.Key;
						}
					}

					// Prioritize the closest exact pixel match between Agents and Stalls
					if (bestAgentId != null && (bestStallIndex == -1 || minAgentDist < minStallDist)) 
					{
						_selectedAgentId = bestAgentId;
						_selectedStallIndex = -1;
					} 
					else if (bestStallIndex != -1) 
					{
						_selectedStallIndex = bestStallIndex;
						_selectedAgentId = null;
					} 
					else 
					{
						// Clicked empty space
						_selectedAgentId = null;
						_selectedStallIndex = -1;
					}
				}
			}
			// Middle or right mouse button to rotate!
			else if (mouseBtnEvent.ButtonIndex == MouseButton.Middle || mouseBtnEvent.ButtonIndex == MouseButton.Right)
			{
				_isRotating = mouseBtnEvent.Pressed;
			}
		}
		else if (@event is InputEventMouseMotion mouseMotionEvent)
		{
			if (_isPanning)
			{
				// Panning: We move the camera in the plane
				camera.Translate(new Vector3(-mouseMotionEvent.Relative.X * 0.1f, mouseMotionEvent.Relative.Y * 0.1f, 0));
			}
			else if (_isRotating)
			{
				// Rotation like a drone / an RTS game
				Vector3 rot = camera.Rotation;
				
				// Turn left/right (Global Y Axis)
				rot.Y -= mouseMotionEvent.Relative.X * 0.005f;
				// Tilt up/down (Local X Axis)
				rot.X -= mouseMotionEvent.Relative.Y * 0.005f;
				
				// Prevents flip-over: max straight ahead (0) and max vertically down (-90 degrees or -PI/2)
				rot.X = Mathf.Clamp(rot.X, -Mathf.Pi / 2.0f, 0.0f);
				
				camera.Rotation = rot;
			}
		}
	}

	private string AbbreviateStallName(string name)
	{
		if (string.IsNullOrEmpty(name)) return name;
		
		return name.Replace("Feuertonne", "FT")
				   .Replace("Verkaufsstand", "VS")
				   .Replace("Glühweinstand", "GW")
				   .Replace("Gastronomie", "G")
				   .Replace("Toilette", "WC")
				   .Replace("Geldautomat", "ATM");
	}


}
