using Godot;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;



public partial class PlaybackVisualizer : Node3D
{
	private string connString = "Host=localhost;Port=5432;Username=mars;Password=anomaly;Database=mars";
	private NpgsqlConnection _connection;




	private void LoadTick(long tick)
	{
		foreach (var a in _activeAgents.Values) a.ActiveThisTick = false;

		string sql = "SELECT id, x, y, hunger, thirst, mood, budget, exhaustion, bladder " +
					 "FROM rathausmarkt_2024.desiremarkettraveler WHERE tick = @tick;";

		using var cmd = new NpgsqlCommand(sql, _connection);
		cmd.Parameters.AddWithValue("tick", tick);
		using var reader = cmd.ExecuteReader();
		
		while (reader.Read())
		{
			string id = reader.GetValue(0).ToString();
			double x = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);
			double y = reader.IsDBNull(2) ? 0 : reader.GetDouble(2);
			double hunger = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);
			double thirst = reader.IsDBNull(4) ? 0 : reader.GetDouble(4);
			double mood = reader.IsDBNull(5) ? 0 : reader.GetDouble(5);
			double budget = reader.IsDBNull(6) ? 0 : reader.GetDouble(6);
			double exhaustion = reader.IsDBNull(7) ? 0 : reader.GetDouble(7);
			double bladder = reader.IsDBNull(8) ? 0 : reader.GetDouble(8);
			
			int hash = id.GetHashCode();
			// Deterministic offset (-0.4m to +0.4m), so waiting agents at stalls
			// or on paths don't overlap 100% (causes black artifacts due to Z-Fighting)
			float offsetX = (Math.Abs(hash) % 100 / 100.0f - 0.5f) * 0.8f; 
			float offsetZ = (Math.Abs(hash / 100) % 100 / 100.0f - 0.5f) * 0.8f;
			
			Vector3 target = LonLatToLocalPos3D(x, y, MapZoom) + new Vector3(offsetX, 0, offsetZ);
			
			if (_activeAgents.TryGetValue(id, out var agent))
			{
				agent.TargetPos = target;
				agent.ActiveThisTick = true;
				
				// Update DB values dynamically during run
				agent.Hunger = hunger;
				agent.Thirst = thirst;
				agent.Mood = mood;
				agent.Budget = budget;
				agent.Exhaustion = exhaustion;
				agent.Bladder = bladder;
				
				// On large jumps in timeline (Scrubbing) -> port directly
				if (agent.CurrentPos.DistanceSquaredTo(target) > 50.0f) {
					agent.CurrentPos = target;
					// When scrubbing we immediately give them the new orientation to prevent ghosting
					agent.TargetPos = target; // target update
				}
			}
			else
			{
				// New agent spawned
				var newAgent = new VisualAgent {
					Id = id,
					IsMale = Math.Abs(id.GetHashCode()) % 2 == 0,
					CurrentPos = target,
					TargetPos = target,
					WalkCycle = (float)GD.RandRange(0.0, Math.PI * 2.0),
					ActiveThisTick = true,
					// Random rotation, so they don't look rigidly in one direction when starting/pausing
					CurrentRot = new Basis(Vector3.Up, (float)GD.RandRange(0.0, Math.PI * 2.0)),
					
					// Initialize DB values on spawn
					Hunger = hunger,
					Thirst = thirst,
					Mood = mood,
					Budget = budget,
					Exhaustion = exhaustion,
					Bladder = bladder
				};
				_activeAgents[id] = newAgent;
			}
		}

		// Delete outdated agents that disappeared
		var keysToRemove = new List<string>();
		foreach (var kvp in _activeAgents)
		{
			if (!kvp.Value.ActiveThisTick) keysToRemove.Add(kvp.Key);
		}
		foreach (var key in keysToRemove) _activeAgents.Remove(key);

		UpdateInfoLabel();
	}


}
