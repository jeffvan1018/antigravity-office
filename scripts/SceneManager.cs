using Godot;

public partial class SceneManager : Node
{
	public static SceneManager Instance { get; private set; }

	[Export]
	public PackedScene PlayerScene { get; set; }

	public string TargetSpawnName { get; private set; } = "SpawnLeft";
	public Node2D CurrentPlayer { get; private set; }

	public override void _Ready()
	{
		Instance = this;

		// Path matches your exact folder and casing
		string path = "res://scenes/characters/player.tscn";

		if (PlayerScene == null)
		{
			if (ResourceLoader.Exists(path))
			{
				PlayerScene = GD.Load<PackedScene>(path);
				GD.Print($"[SceneManager] Loaded player scene successfully from {path}");
			}
			else
			{
				GD.PrintErr($"[SceneManager] ERROR: File does not exist at {path}!");
			}
		}
	}

	public void ChangeRoom(string scenePath, string targetSpawnMarker)
	{
		if (string.IsNullOrEmpty(scenePath))
		{
			GD.PrintErr("[SceneManager] ERROR: Target scene path is empty.");
			return;
		}

		TargetSpawnName = targetSpawnMarker;
		GD.Print($"[SceneManager] Changing room to: {scenePath} (Target: {targetSpawnMarker})");

		Callable.From(() => GetTree().ChangeSceneToFile(scenePath)).CallDeferred();
	}

	public void SetupPlayerInRoom(BaseRoom room)
	{
		// 1. Look for an existing valid player inside this specific room
		CurrentPlayer = room.GetNodeOrNull<Node2D>("Player");
		if (CurrentPlayer == null)
		{
			foreach (Node child in room.GetChildren())
			{
				if (child.IsInGroup("player") && GodotObject.IsInstanceValid(child) && !child.IsQueuedForDeletion())
				{
					CurrentPlayer = (Node2D)child;
					break;
				}
			}
		}

		// 2. If the room has no player, spawn one from the loaded scene
		if (CurrentPlayer == null)
		{
			if (PlayerScene != null)
			{
				CurrentPlayer = PlayerScene.Instantiate<Node2D>();
				CurrentPlayer.Name = "Player";
				room.AddChild(CurrentPlayer);
				GD.Print($"[SceneManager] Spawned fresh player instance into {room.Name}");
			}
			else
			{
				GD.PrintErr("[SceneManager] ERROR: Cannot spawn player because PlayerScene is null!");
				return;
			}
		}

		// 3. Find target spawn marker
		Marker2D marker = room.GetNodeOrNull<Marker2D>(TargetSpawnName)
					   ?? room.GetNodeOrNull<Marker2D>("SpawnLeft")
					   ?? room.GetNodeOrNull<Marker2D>("SpawnRight");

		// 4. Snap position
		if (marker != null)
		{
			CurrentPlayer.GlobalPosition = marker.GlobalPosition;
			GD.Print($"[SceneManager] Positioned player at {marker.Name}: {marker.GlobalPosition}");
		}
		else
		{
			GD.PrintErr($"[SceneManager] WARNING: No spawn marker found in {room.Name}!");
		}
	}
}
