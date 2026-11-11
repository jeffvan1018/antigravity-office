using Godot;

public partial class DialogueLayer : CanvasLayer
{
	public static DialogueLayer Instance { get; private set; }

	private PanelContainer _bubble;
	private Label _text;
	private SceneTreeTimer _hideTimer;
	private Node2D _trackedTarget;

	public override void _Ready()
	{
		Instance = this;
		_bubble = GetNode<PanelContainer>("Bubble");
		_text = GetNode<Label>("Bubble/Text");
		_bubble.Hide();

		var agy = GetNodeOrNull<AgyProcessManager>("/root/AgyProcessManager") 
			   ?? AgyProcessManager.Instance;

		if (agy != null)
		{
			agy.TurnStarted += OnTurnStarted;
			agy.ChunkReceived += OnChunkReceived;
			agy.MessageReceived += ShowAgyResponse;
			GD.Print("[DialogueLayer] Successfully hooked to AgyProcessManager signals");
		}
	}

	private void OnTurnStarted()
	{
		EnsureTarget();
		_hideTimer?.Dispose();
		_text.Text = "...";
		_bubble.ResetSize();
		_bubble.Show();
		UpdateBubblePosition();
	}

	private void OnChunkReceived(string delta)
	{
		EnsureTarget();
		if (_text.Text == "...")
		{
			_text.Text = "";
		}

		_text.Text += delta;
		_bubble.ResetSize();
		_bubble.Show();
		UpdateBubblePosition();
	}

	public void ShowAgyResponse(string message)
	{
		EnsureTarget();

		_text.Text = message;
		_bubble.ResetSize();
		_bubble.Show();
		UpdateBubblePosition();

		// Cancel previous timer if speech updates rapidly
		_hideTimer?.Dispose();

		// Display time scales with length (minimum 3 seconds)
		float displayTime = Mathf.Max(3.0f, message.Length * 0.06f);
		_hideTimer = GetTree().CreateTimer(displayTime);
		_hideTimer.Timeout += () => _bubble.Hide();
	}

	public override void _Process(double delta)
	{
		if (_bubble.Visible)
		{
			UpdateBubblePosition();
		}
	}

	private void EnsureTarget()
	{
		// Safely check if the current target has been freed or is being deleted
		if (!GodotObject.IsInstanceValid(_trackedTarget) || !_trackedTarget.IsInsideTree() || _trackedTarget.IsQueuedForDeletion())
		{
			_trackedTarget = null;
			var players = GetTree().GetNodesInGroup("player");
			foreach (var node in players)
			{
				if (GodotObject.IsInstanceValid(node) && node is Node2D n2d && n2d.IsInsideTree() && !n2d.IsQueuedForDeletion())
				{
					_trackedTarget = n2d;
					break;
				}
			}
		}
	}

	private void UpdateBubblePosition()
	{
		EnsureTarget();

		if (GodotObject.IsInstanceValid(_trackedTarget) && _trackedTarget.IsInsideTree())
		{
			// Convert character world space coordinates to UI canvas coordinates
			Vector2 screenPos = _trackedTarget.GetGlobalTransformWithCanvas().Origin;

			// Character is ~128px tall; place bubble ~140px above feet
			Vector2 bubbleOffset = new Vector2(-_bubble.Size.X / 2.0f, -145.0f);
			_bubble.Position = screenPos + bubbleOffset;
		}
	}
}
