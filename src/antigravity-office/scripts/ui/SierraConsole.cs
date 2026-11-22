using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace AntigravityOffice;

public enum LocalCommandKind
{
    None,
    Pwd,
    Workspace
}

public partial class SierraConsole : Control
{
    private const float MinHeight = 84f;
    private const float MaxViewportFraction = 0.90f;

    private Control _resizeHandle = null!;
    private Label _statusBadge = null!;
    private Button _workspaceButton = null!;
    private Button _clearButton = null!;
    private Button _maximizeButton = null!;
    private RichTextLabel _outputLog = null!;
    private LineEdit _commandInput = null!;
    private FileDialog _workspaceDialog = null!;

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
        _workspaceButton = GetNode<Button>("Frame/MarginContainer/VBoxContainer/HeaderBar/WorkspaceButton");
        _clearButton = GetNode<Button>("Frame/MarginContainer/VBoxContainer/HeaderBar/ClearButton");
        _maximizeButton = GetNode<Button>("Frame/MarginContainer/VBoxContainer/HeaderBar/MaximizeButton");
        _outputLog = GetNode<RichTextLabel>("Frame/MarginContainer/VBoxContainer/OutputLog");
        _commandInput = GetNode<LineEdit>("Frame/MarginContainer/VBoxContainer/InputRow/CommandInput");
        _workspaceDialog = GetNode<FileDialog>("WorkspaceFileDialog");

        _resizeHandle.GuiInput += OnResizeHandleGuiInput;
        _workspaceButton.Pressed += OnWorkspaceButtonPressed;
        _clearButton.Pressed += OnClearPressed;
        _maximizeButton.Pressed += OnMaximizePressed;
        _commandInput.TextSubmitted += OnCommandSubmitted;
        _commandInput.GuiInput += OnCommandInputGuiInput;
        _workspaceDialog.DirSelected += OnWorkspaceDirSelected;

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
            _agyBridge.SessionReset += OnSessionReset;
        }

        UpdateWorkspaceIndicator();
        _outputLog.AppendText("[color=#7aa2f7]Antigravity Office Terminal Ready. Press '/' or Enter to type, Esc to pan rooms.[/color]\n");
    }

    public override void _ExitTree()
    {
        if (_viewport is not null && GodotObject.IsInstanceValid(_viewport))
        {
            _viewport.SizeChanged -= OnViewportSizeChanged;
        }

        if (_workspaceDialog is not null && GodotObject.IsInstanceValid(_workspaceDialog))
        {
            _workspaceDialog.DirSelected -= OnWorkspaceDirSelected;
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
            _agyBridge.SessionReset -= OnSessionReset;
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

        if (TryParseLocalCommand(text, out LocalCommandKind cmdKind, out string cmdArg))
        {
            HandleLocalCommand(cmdKind, cmdArg);
            return;
        }

        _statusBadge.Text = "[AGY: THINKING]";
        _agyBridge?.SendPrompt(text);
    }

    public static bool TryParseLocalCommand(string input, out LocalCommandKind kind, out string argument)
    {
        kind = LocalCommandKind.None;
        argument = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string trimmed = input.Trim();
        if (trimmed.Equals("/pwd", StringComparison.OrdinalIgnoreCase))
        {
            kind = LocalCommandKind.Pwd;
            return true;
        }

        if (trimmed.Equals("/workspace", StringComparison.OrdinalIgnoreCase))
        {
            kind = LocalCommandKind.Workspace;
            return true;
        }

        if (trimmed.StartsWith("/workspace", StringComparison.OrdinalIgnoreCase) &&
            trimmed.Length > "/workspace".Length &&
            char.IsWhiteSpace(trimmed["/workspace".Length]))
        {
            kind = LocalCommandKind.Workspace;
            argument = trimmed["/workspace".Length..].Trim();
            return true;
        }

        return false;
    }

    public static string FormatWorkspaceLabel(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return "📁 (none)";
        }

        string trimmed = workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(trimmed))
        {
            return $"📁 {workingDirectory}";
        }

        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? $"📁 {trimmed}" : $"📁 {name}";
    }

    private void HandleLocalCommand(LocalCommandKind kind, string argument)
    {
        string currentDir = _agyBridge?.WorkingDirectory ?? string.Empty;
        switch (kind)
        {
            case LocalCommandKind.Pwd:
                string pwdDisplay = string.IsNullOrEmpty(currentDir) ? "(unset)" : currentDir;
                _outputLog.AppendText($"[color=#7aa2f7]Workspace: {EscapeBbcode(pwdDisplay)}[/color]\n");
                break;

            case LocalCommandKind.Workspace:
                if (string.IsNullOrWhiteSpace(argument))
                {
                    string currentDisplay = string.IsNullOrEmpty(currentDir) ? "(unset)" : currentDir;
                    _outputLog.AppendText($"[color=#7aa2f7]Usage: /workspace <path> (current: {EscapeBbcode(currentDisplay)})[/color]\n");
                    return;
                }

                _agyBridge?.SwitchWorkspace(argument);
                break;
        }
    }

    private void OnWorkspaceButtonPressed()
    {
        string currentDir = _agyBridge?.WorkingDirectory ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(currentDir) && Directory.Exists(currentDir))
        {
            _workspaceDialog.CurrentDir = currentDir;
        }

        _workspaceDialog.PopupCenteredRatio(0.65f);
    }

    private void OnWorkspaceDirSelected(string dir)
    {
        _agyBridge?.SwitchWorkspace(dir);
    }

    private void UpdateWorkspaceIndicator()
    {
        string workingDir = _agyBridge?.WorkingDirectory ?? string.Empty;
        _workspaceButton.Text = FormatWorkspaceLabel(workingDir);
        _workspaceButton.TooltipText = string.IsNullOrEmpty(workingDir) ? "Select workspace directory" : workingDir;
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
        if (_agyBridge is null || !_agyBridge.IsRunning)
        {
            _statusBadge.Text = "[AGY: ERROR]";
        }
        _outputLog.AppendText($"[color=#f7768e]! {EscapeBbcode(message)}[/color]\n");
    }

    private void OnSessionReset()
    {
        CloseStreamingLineIfNeeded();
        _statusBadge.Text = "[AGY: READY]";
        UpdateWorkspaceIndicator();
        string workingDir = _agyBridge?.WorkingDirectory ?? string.Empty;
        if (!string.IsNullOrEmpty(workingDir))
        {
            _outputLog.AppendText($"[color=#7aa2f7]Workspace switched to: {EscapeBbcode(workingDir)}[/color]\n");
        }
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
