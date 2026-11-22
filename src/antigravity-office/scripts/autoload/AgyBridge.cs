using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Godot;

namespace AntigravityOffice;

public enum AgyEventKind
{
    Init,
    AgentResponse,
    ToolStep,
    SubagentUpdate,
    Result,
    RawOutput
}

public sealed record AgyParsedEvent(
    AgyEventKind Kind,
    string ConversationId = "",
    string TextDelta = "",
    string ToolName = "",
    string ToolState = "",
    string SubagentId = "",
    string SubagentName = "",
    string SubagentState = "",
    string Status = "",
    string Error = "",
    string Response = "",
    string RawLine = ""
);

public partial class AgyBridge : Node
{
    [Signal]
    public delegate void SessionInitializedEventHandler(string conversationId);

    [Signal]
    public delegate void AgentTextDeltaEventHandler(string conversationId, string textDelta);

    [Signal]
    public delegate void ToolStepUpdatedEventHandler(string conversationId, string toolName, string state);

    [Signal]
    public delegate void SubagentUpdatedEventHandler(string conversationId, string subagentId, string name, string state);

    [Signal]
    public delegate void TurnCompletedEventHandler(string conversationId, string status, string error, string response);

    [Signal]
    public delegate void RawOutputReceivedEventHandler(string line);

    [Signal]
    public delegate void ProcessErrorEventHandler(string message);

    [Export]
    public bool AutoStart { get; set; } = true;

    private Process? _agyProcess;
    private StreamWriter? _stdinWriter;
    private readonly object _writeLock = new();
    private bool _shuttingDown;

    public bool IsRunning => _agyProcess is { HasExited: false };

    public override void _Ready()
    {
        AppDomain.CurrentDomain.ProcessExit += OnDomainProcessExit;

        if (AutoStart && !DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            StartProcess();
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
        {
            ShutdownProcess();
        }
    }

    public override void _ExitTree()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnDomainProcessExit;
        ShutdownProcess();
    }

    private void OnDomainProcessExit(object? sender, EventArgs e)
    {
        ShutdownProcess();
    }

    public bool StartProcess()
    {
        if (IsRunning)
        {
            return true;
        }

        _shuttingDown = false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "stdbuf",
                Arguments = "-i0 -oL -eL agy --input-format stream-json --output-format stream-json",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            string homeDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            string localBin = Path.Combine(homeDir, ".local", "bin");
            string currentPath = startInfo.EnvironmentVariables["PATH"] ?? System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (!string.IsNullOrEmpty(localBin) && !currentPath.Contains(localBin, StringComparison.Ordinal))
            {
                startInfo.EnvironmentVariables["PATH"] = string.IsNullOrEmpty(currentPath)
                    ? localBin
                    : $"{localBin}{Path.PathSeparator}{currentPath}";
            }

            _agyProcess = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            _agyProcess.OutputDataReceived += OnOutputDataReceived;
            _agyProcess.ErrorDataReceived += OnErrorDataReceived;

            if (!_agyProcess.Start())
            {
                EmitProcessError("Failed to start stdbuf/agy process.");
                return false;
            }

            _stdinWriter = new StreamWriter(_agyProcess.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            _agyProcess.BeginOutputReadLine();
            _agyProcess.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            EmitProcessError($"Error starting agy process: {ex.Message}");
            return false;
        }
    }

    public void SendPrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!IsRunning && !StartProcess())
        {
            return;
        }

        string payload = SerializePrompt(text);
        lock (_writeLock)
        {
            if (_stdinWriter is null)
            {
                return;
            }

            try
            {
                _stdinWriter.WriteLine(payload);
            }
            catch (Exception ex)
            {
                EmitProcessError($"Failed to write prompt to agy stdin: {ex.Message}");
            }
        }
    }

    public static string SerializePrompt(string text)
    {
        var payload = new
        {
            @event = "user",
            message = new
            {
                role = "user",
                content = text
            }
        };
        return JsonSerializer.Serialize(payload);
    }

    public static IReadOnlyList<AgyParsedEvent> ParseNdjsonLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return Array.Empty<AgyParsedEvent>();
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new[] { new AgyParsedEvent(AgyEventKind.RawOutput, RawLine: line) };
            }

            string eventName = GetString(root, "event");
            string conversationId = GetString(root, "conversation_id");

            switch (eventName)
            {
                case "init":
                    return new[] { new AgyParsedEvent(AgyEventKind.Init, ConversationId: conversationId) };

                case "step_update":
                {
                    if (!root.TryGetProperty("step_update", out JsonElement stepUpdate) || stepUpdate.ValueKind != JsonValueKind.Object)
                    {
                        return new[] { new AgyParsedEvent(AgyEventKind.RawOutput, RawLine: line) };
                    }

                    if (string.IsNullOrEmpty(conversationId))
                    {
                        conversationId = GetString(stepUpdate, "conversation_id");
                    }

                    var events = new List<AgyParsedEvent>();
                    string stepType = GetString(stepUpdate, "step_type");

                    if (stepType == "agent_response")
                    {
                        string textDelta = GetString(stepUpdate, "text_delta");
                        events.Add(new AgyParsedEvent(
                            AgyEventKind.AgentResponse,
                            ConversationId: conversationId,
                            TextDelta: textDelta));
                    }
                    else if (stepType == "tool")
                    {
                        string toolName = GetString(stepUpdate, "tool_name");
                        string state = GetString(stepUpdate, "state");
                        events.Add(new AgyParsedEvent(
                            AgyEventKind.ToolStep,
                            ConversationId: conversationId,
                            ToolName: toolName,
                            ToolState: state));
                    }

                    if (stepUpdate.TryGetProperty("subagent_info", out JsonElement subagentInfo) &&
                        subagentInfo.ValueKind == JsonValueKind.Object)
                    {
                        string subagentId = GetFirstString(subagentInfo, "subagent_id", "id", "conversation_id");
                        string subagentName = GetFirstString(subagentInfo, "name", "agent_name", "role");
                        string subagentState = GetFirstString(subagentInfo, "state", "status");
                        events.Add(new AgyParsedEvent(
                            AgyEventKind.SubagentUpdate,
                            ConversationId: conversationId,
                            SubagentId: subagentId,
                            SubagentName: subagentName,
                            SubagentState: subagentState));
                    }

                    if (events.Count == 0)
                    {
                        events.Add(new AgyParsedEvent(AgyEventKind.RawOutput, RawLine: line));
                    }

                    return events;
                }

                case "result":
                {
                    if (!root.TryGetProperty("result", out JsonElement resultObj) || resultObj.ValueKind != JsonValueKind.Object)
                    {
                        return new[] { new AgyParsedEvent(AgyEventKind.RawOutput, RawLine: line) };
                    }

                    if (string.IsNullOrEmpty(conversationId))
                    {
                        conversationId = GetString(resultObj, "conversation_id");
                    }

                    string status = GetString(resultObj, "status");
                    string error = GetString(resultObj, "error");
                    string response = GetString(resultObj, "response");
                    return new[]
                    {
                        new AgyParsedEvent(
                            AgyEventKind.Result,
                            ConversationId: conversationId,
                            Status: status,
                            Error: error,
                            Response: response)
                    };
                }

                default:
                    return new[] { new AgyParsedEvent(AgyEventKind.RawOutput, RawLine: line) };
            }
        }
        catch (JsonException)
        {
            return new[] { new AgyParsedEvent(AgyEventKind.RawOutput, RawLine: line) };
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        IReadOnlyList<AgyParsedEvent> parsedEvents = ParseNdjsonLine(e.Data);
        foreach (AgyParsedEvent evt in parsedEvents)
        {
            DispatchParsedEvent(evt);
        }
    }

    private void DispatchParsedEvent(AgyParsedEvent evt)
    {
        switch (evt.Kind)
        {
            case AgyEventKind.Init:
                Callable.From(() => EmitSignal(SignalName.SessionInitialized, evt.ConversationId)).CallDeferred();
                break;

            case AgyEventKind.AgentResponse:
                Callable.From(() => EmitSignal(SignalName.AgentTextDelta, evt.ConversationId, evt.TextDelta)).CallDeferred();
                break;

            case AgyEventKind.ToolStep:
                Callable.From(() => EmitSignal(SignalName.ToolStepUpdated, evt.ConversationId, evt.ToolName, evt.ToolState)).CallDeferred();
                break;

            case AgyEventKind.SubagentUpdate:
                Callable.From(() => EmitSignal(SignalName.SubagentUpdated, evt.ConversationId, evt.SubagentId, evt.SubagentName, evt.SubagentState)).CallDeferred();
                break;

            case AgyEventKind.Result:
                Callable.From(() => EmitSignal(SignalName.TurnCompleted, evt.ConversationId, evt.Status, evt.Error, evt.Response)).CallDeferred();
                break;

            case AgyEventKind.RawOutput:
                Callable.From(() => EmitSignal(SignalName.RawOutputReceived, evt.RawLine)).CallDeferred();
                break;
        }
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        string line = e.Data;
        Callable.From(() => EmitSignal(SignalName.RawOutputReceived, $"[stderr] {line}")).CallDeferred();
    }

    private void EmitProcessError(string message)
    {
        GD.PushWarning($"[AgyBridge] {message}");
        Callable.From(() => EmitSignal(SignalName.ProcessError, message)).CallDeferred();
    }

    public void ShutdownProcess()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;

        lock (_writeLock)
        {
            try
            {
                _stdinWriter?.Dispose();
            }
            catch
            {
                // Ignore disposal errors during shutdown.
            }
            _stdinWriter = null;
        }

        if (_agyProcess is null)
        {
            return;
        }

        try
        {
            _agyProcess.OutputDataReceived -= OnOutputDataReceived;
            _agyProcess.ErrorDataReceived -= OnErrorDataReceived;

            if (!_agyProcess.HasExited)
            {
                _agyProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore errors if process already exited.
        }
        finally
        {
            _agyProcess.Dispose();
            _agyProcess = null;
        }
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement prop))
        {
            return string.Empty;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.GetRawText(),
            _ => string.Empty
        };
    }

    private static string GetFirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (string name in propertyNames)
        {
            string val = GetString(element, name);
            if (!string.IsNullOrEmpty(val))
            {
                return val;
            }
        }

        return string.Empty;
    }
}
