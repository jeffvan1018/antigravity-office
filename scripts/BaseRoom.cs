using Godot;

public partial class BaseRoom : Node2D
{
	[ExportGroup("Left Doorway")]
	[Export(PropertyHint.File, "*.tscn")]
	public string LeftRoomScene { get; set; } = "";

	[Export]
	public string LeftTargetSpawn { get; set; } = "SpawnRight";

	[ExportGroup("Right Doorway")]
	[Export(PropertyHint.File, "*.tscn")]
	public string RightRoomScene { get; set; } = "";

	[Export]
	public string RightTargetSpawn { get; set; } = "SpawnLeft";

	public override void _Ready()
	{
		var exitLeft = GetNodeOrNull<Area2D>("ExitLeft");
		if (exitLeft != null)
		{
			exitLeft.BodyEntered += OnExitLeftEntered;
		}

		var exitRight = GetNodeOrNull<Area2D>("ExitRight");
		if (exitRight != null)
		{
			exitRight.BodyEntered += OnExitRightEntered;
		}

		// Hands off player placement / instancing to SceneManager
		SceneManager.Instance?.SetupPlayerInRoom(this);
	}

	private void PositionPlayer()
	{
		// Delegate spawning and positioning to SceneManager
		SceneManager.Instance?.SetupPlayerInRoom(this);
	}

	private void OnExitLeftEntered(Node2D body)
	{
		if (body.IsInGroup("player") && !string.IsNullOrEmpty(LeftRoomScene))
		{
			SceneManager.Instance.ChangeRoom(LeftRoomScene, LeftTargetSpawn);
		}
	}

	private void OnExitRightEntered(Node2D body)
	{
		if (body.IsInGroup("player") && !string.IsNullOrEmpty(RightRoomScene))
		{
			SceneManager.Instance.ChangeRoom(RightRoomScene, RightTargetSpawn);
		}
	}
}
