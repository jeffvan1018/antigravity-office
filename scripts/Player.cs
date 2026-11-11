using Godot;

public partial class Player : CharacterBody2D
{
	[Export] public float Speed { get; set; } = 200.0f;

	private NavigationAgent2D _navAgent;
	private AnimatedSprite2D _anim;

	public override void _Ready()
	{
		AddToGroup("player");

		_navAgent = GetNode<NavigationAgent2D>("NavigationAgent2D");
		_anim = GetNode<AnimatedSprite2D>("AnimatedSprite2D");

		// Start facing forward/idle
		_anim.Play("idle");
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb && mb.Pressed)
		{
			if (mb.ButtonIndex == MouseButton.Left)
			{
				// Normal walking
				_navAgent.TargetPosition = GetGlobalMousePosition();
			}
			else if (mb.ButtonIndex == MouseButton.Right)
			{
				GD.Print("[Player] Right-click: sending structured message to agy...");
				AgyProcessManager.Instance?.SendJsonMessage("chat", "hello");
			}
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		// 1. Destination reached: stop and switch to idle
		if (_navAgent.IsNavigationFinished())
		{
			Velocity = Vector2.Zero;
			_anim.Play("idle");
			MoveAndSlide();
			return;
		}

		// 2. Calculate movement vector
		Vector2 nextPathPos = _navAgent.GetNextPathPosition();
		Vector2 direction = (nextPathPos - GlobalPosition).Normalized();

		Velocity = direction * Speed;

		// 3. Drive directional animations
		UpdateAnimation(direction);

		MoveAndSlide();
	}

	private void UpdateAnimation(Vector2 dir)
	{
		// Check if horizontal movement is stronger than vertical movement
		if (Mathf.Abs(dir.X) > Mathf.Abs(dir.Y))
		{
			_anim.Play("walk_right");
			// Flip the sprite horizontally when moving left
			_anim.FlipH = dir.X < 0;
		}
		else
		{
			// Vertical movement: reset FlipH so up/down don't stay mirrored
			_anim.FlipH = false;

			if (dir.Y > 0)
			{
				_anim.Play("walk_down"); // Walking toward camera
			}
			else
			{
				_anim.Play("walk_up");   // Walking away toward back wall
			}
		}
	}
}
