using System;
using System.Collections.Generic;
using Godot;

namespace AntigravityOffice;

public partial class SierraConsole : Control
{
    private const float MinHeight = 84f;
    private const float MaxViewportFraction = 0.90f;

    private Control _resizeHandle = null!;
    private Label _statusBadge = null!;
    private Button _clearButton = null!;
    private Button _maximizeButton = null!;
    private RichTextLabel _outputLog = null!;
    private LineEdit _commandInput = null!;

    private AgyBridge? _agyBridge;
    private Viewport? _viewport;

    private float _restoredHeight = 156f;
    private bool _isMaximized;
    private bool _isDragging;
    private bool _streamingLineOpen;

    private readonly List<string> _history = new();
    private int _historyIndex = -1;

    public bool HasInputFocus => _commandInput?.HasFocus() ?? false;

    public override void _Ready()
    {
        _resizeHandle = GetNode<Control>("ResizeHandle");
        _statusBadge = GetNode<Label>("Frame/MarginContainer/VBoxContainer/HeaderBar/StatusBadge");
        _clearButton = GetNode<Button>("Frame/MarginContainer/VBoxContainer/HeaderBar/ClearButton");
        _maximizeButton = GetNode<Button>("Frame/MarginContainer/VBoxContainer/HeaderBar/MaximizeButton");
        _outputLog = GetNode<RichTextLabel>("Frame/MarginContainer/VBoxContainer/OutputLog");
        _commandInput = GetNode<LineEdit>("Frame/MarginContainer/VBoxContainer/InputRow/CommandInput");

        _resizeHandle.GuiInput += OnResizeHandleGuiInput;
        _clearButton.Pressed += OnClearPressed;
        _maximizeButton.Pressed += OnMaximizePressed;
        _commandInput.TextSubmitted += OnCommandSubmitted;
        _commandInput.GuiInput += OnCommandInputGuiInput;

        _viewport = GetViewport();
        if (_viewport is not null)
        {
            _viewport.SizeChanged += OnViewportSizeChanged;
        }

        OnViewportSizeChanged();

        _agyBridge = GetNodeOrNull<AgyBridge>("/root/AgyBridge");
        if (_agyBridge is not null)
        {
            _agyBridge.SessionInitialized += OnSessionInitialized;
            _agyBridge.AgentTextDelta += OnAgentTextDelta;
            _agyBridge.ToolStepUpdated += OnToolStepUpdated;
            _agyBridge.SubagentUpdated += OnSubagentUpdated;
            _agyBridge.TurnCompleted += OnTurnCompleted;
            _agyBridge.RawOutputReceived += OnRawOutputReceived;
            _agyBridge.ProcessError += OnProcessError;
        }

        _outputLog.AppendText("[color=#7aa2f7]Antigravity Office Terminal Ready. Press '/' or Enter to type, Esc to pan rooms.[/color]\n");
    }

    public override void _ExitTree()
    {
        if (_viewport is not null && GodotObject.IsInstanceValid(_viewport))
        {
            _viewport.SizeChanged -= OnViewportSizeChanged;
        }

        if (_agyBridge is not null)
        {
            _agyBridge.SessionInitialized -= OnSessionInitialized;
            _agyBridge.AgentTextDelta -= OnAgentTextDelta;
            _agyBridge.ToolStepUpdated -= OnToolStepUpdated;
            _agyBridge.SubagentUpdated -= OnSubagentUpdated;
            _agyBridge.TurnCompleted -= OnTurnCompleted;
            _agyBridge.RawOutputReceived -= OnRawOutputReceived;
            _agyBridge.ProcessError -= OnProcessError;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
        {
            return;
        }

        if (keyEvent.Keycode == Key.Escape && _commandInput.HasFocus())
        {
            _commandInput.ReleaseFocus();
            GetViewport().SetInputAsHandled();
        }
        else if ((keyEvent.Keycode == Key.Slash || keyEvent.Keycode == Key.Enter || keyEvent.Keycode == Key.KpEnter) &&
                 !_commandInput.HasFocus())
        {
            _commandInput.GrabFocus();
            GetViewport().SetInputAsHandled();
        }
    }

    private void OnResizeHandleGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
        {
            _isDragging = mouseButton.Pressed;
        }
        else if (@event is InputEventMouseMotion mouseMotion && _isDragging)
        {
            float maxHeight = GetMaxPanelHeight();
            float nextHeight = Mathf.Clamp(CustomMinimumSize.Y - mouseMotion.Relative.Y, MinHeight, maxHeight);
            _isMaximized = false;
            _restoredHeight = nextHeight;
            _maximizeButton.Text = "▲ MAX";
            ApplyPanelHeight(nextHeight);
        }
    }

    private void OnClearPressed()
    {
        _outputLog.Clear();
        _streamingLineOpen = false;
    }

    private void OnMaximizePressed()
    {
        _isMaximized = !_isMaximized;
        if (_isMaximized)
        {
            _maximizeButton.Text = "▼ RESTORE";
            ApplyPanelHeight(GetMaxPanelHeight());
        }
        else
        {
            _maximizeButton.Text = "▲ MAX";
            ApplyPanelHeight(Mathf.Clamp(_restoredHeight, MinHeight, GetMaxPanelHeight()));
        }
    }

    private void OnCommandSubmitted(string rawText)
    {
        string text = rawText.Trim();
        _commandInput.Clear();

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        CloseStreamingLineIfNeeded();
        _outputLog.AppendText($"[color=#ffcc44]> {EscapeBbcode(text)}[/color]\n");

        if (_history.Count == 0 || _history[^1] != text)
        {
            _history.Add(text);
        }
        _historyIndex = _history.Count;

        _statusBadge.Text = "[AGY: THINKING]";
        _agyBridge?.SendPrompt(text);
    }

    private void OnCommandInputGuiInput(InputEvent @event)
    {
        if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
        {
            return;
        }

        if (keyEvent.Keycode == Key.Escape)
        {
            _commandInput.ReleaseFocus();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (keyEvent.Keycode == Key.Up && _history.Count > 0)
        {
            _historyIndex = Math.Max(0, _historyIndex - 1);
            _commandInput.Text = _history[_historyIndex];
            _commandInput.CaretColumn = _commandInput.Text.Length;
            GetViewport().SetInputAsHandled();
        }
        else if (keyEvent.Keycode == Key.Down && _history.Count > 0)
        {
            _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
            _commandInput.Text = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
            _commandInput.CaretColumn = _commandInput.Text.Length;
            GetViewport().SetInputAsHandled();
        }
    }

    private void OnSessionInitialized(string conversationId)
    {
        CloseStreamingLineIfNeeded();
        _statusBadge.Text = "[AGY: READY]";
        _outputLog.AppendText($"[color=#7aa2f7]Session initialized: {EscapeBbcode(conversationId)}[/color]\n");
    }

    private void OnAgentTextDelta(string _conversationId, string textDelta)
    {
        if (string.IsNullOrEmpty(textDelta))
        {
            return;
        }

        _statusBadge.Text = "[AGY: THINKING]";
        _outputLog.AppendText(EscapeBbcode(textDelta));
        _streamingLineOpen = !textDelta.EndsWith('\n');
    }

    private void OnToolStepUpdated(string _conversationId, string toolName, string state)
    {
        CloseStreamingLineIfNeeded();
        _statusBadge.Text = $"[TOOL: {toolName}]";
        _outputLog.AppendText($"[color=#77cc88]⚙ {EscapeBbcode(toolName)} ({EscapeBbcode(state)})[/color]\n");
    }

    private void OnSubagentUpdated(string _conversationId, string subagentId, string name, string state)
    {
        CloseStreamingLineIfNeeded();
        string label = string.IsNullOrEmpty(name) ? subagentId : name;
        _outputLog.AppendText($"[color=#bb9af7]◆ Subagent {EscapeBbcode(label)}: {EscapeBbcode(state)}[/color]\n");
    }

    private void OnTurnCompleted(string _conversationId, string status, string error, string _response)
    {
        CloseStreamingLineIfNeeded();
        _statusBadge.Text = "[AGY: READY]";

        if (string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            string errMsg = string.IsNullOrEmpty(error) ? "Unknown error" : error;
            _outputLog.AppendText($"[color=#f7768e]✖ Error: {EscapeBbcode(errMsg)}[/color]\n");
        }
        else
        {
            _outputLog.AppendText("[color=#565f89]────────────────────────────────────────[/color]\n");
        }
    }

    private void OnRawOutputReceived(string line)
    {
        CloseStreamingLineIfNeeded();
        _outputLog.AppendText($"[color=#9aa5ce]{EscapeBbcode(line)}[/color]\n");
    }

    private void OnProcessError(string message)
    {
        CloseStreamingLineIfNeeded();
        _statusBadge.Text = "[AGY: ERROR]";
        _outputLog.AppendText($"[color=#f7768e]! {EscapeBbcode(message)}[/color]\n");
    }

    private void CloseStreamingLineIfNeeded()
    {
        if (_streamingLineOpen)
        {
            _outputLog.AppendText("\n");
            _streamingLineOpen = false;
        }
    }

    private void OnViewportSizeChanged()
    {
        float maxHeight = GetMaxPanelHeight();
        float targetHeight = _isMaximized ? maxHeight : Mathf.Clamp(_restoredHeight, MinHeight, maxHeight);
        ApplyPanelHeight(targetHeight);
    }

    private float GetMaxPanelHeight()
    {
        float viewportHeight = GetViewportRect().Size.Y;
        if (viewportHeight <= 0f)
        {
            viewportHeight = 768f;
        }

        return Math.Max(MinHeight, viewportHeight * MaxViewportFraction);
    }

    private void ApplyPanelHeight(float height)
    {
        CustomMinimumSize = new Vector2(0f, height);
        OffsetTop = -height;
        OffsetBottom = 0f;
    }

    private static string EscapeBbcode(string text)
    {
        return text.Replace("[", "[lb]");
    }
}
