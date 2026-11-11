using Godot;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

public partial class AgyProcessManager : Node
{
	public static AgyProcessManager Instance { get; private set; }

	[Signal]
	public delegate void MessageReceivedEventHandler(string text);

	[Signal]
	public delegate void ChunkReceivedEventHandler(string delta);

	[Signal]
	public delegate void TurnStartedEventHandler();

	private Process _agyProcess;
	private StreamWriter _stdin;

	public override void _EnterTree()
	{
		Instance = this;
	}

	public override void _Ready()
	{
		AppDomain.CurrentDomain.ProcessExit += (s, e) => TerminateAgy();
		LaunchAgy();
	}

	private void LaunchAgy()
	{
		// stdbuf forces unbuffered stdin (-i0) and line-buffered stdout/stderr (-oL, -eL)
		var startInfo = new ProcessStartInfo
		{
			FileName = "stdbuf",
			Arguments = "-i0 -oL -eL agy --input-format stream-json --output-format stream-json",
			WorkingDirectory = ProjectSettings.GlobalizePath("res://"),
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};

		string currentPath = System.Environment.GetEnvironmentVariable("PATH") ?? "";
		string userLocalBin = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".local", "bin");
		if (!currentPath.Contains(userLocalBin) && Directory.Exists(userLocalBin))
		{
			startInfo.EnvironmentVariables["PATH"] = $"{userLocalBin}:{currentPath}";
		}

		try
		{
			_agyProcess = new Process
			{
				StartInfo = startInfo,
				EnableRaisingEvents = true
			};

			_agyProcess.Exited += (sender, args) =>
			{
				GD.PrintErr($"[AgyProcessManager] Process terminated with exit code: {_agyProcess.ExitCode}");
			};

			_agyProcess.OutputDataReceived += (sender, args) =>
			{
				if (!string.IsNullOrWhiteSpace(args.Data))
				{
					GD.Print($"[AGY RAW STDOUT]: {args.Data}");
					HandleIncomingLine(args.Data);
				}
			};

			_agyProcess.ErrorDataReceived += (sender, args) =>
			{
				if (!string.IsNullOrEmpty(args.Data))
				{
					GD.PrintErr($"[AGY RAW STDERR]: {args.Data}");
				}
			};

			_agyProcess.Start();
			_agyProcess.BeginOutputReadLine();
			_agyProcess.BeginErrorReadLine();

			// Set up StreamWriter with explicit UTF-8 (no BOM) and Unix newlines
			_stdin = new StreamWriter(_agyProcess.StandardInput.BaseStream, new UTF8Encoding(false))
			{
				AutoFlush = true,
				NewLine = "\n"
			};

			GD.Print($"[AgyProcessManager] Successfully spawned 'agy' via stdbuf (PID: {_agyProcess.Id})");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[AgyProcessManager] Failed to launch: {ex.Message}");
		}
	}

	private void HandleIncomingLine(string rawLine)
	{
		try
		{
			using var doc = JsonDocument.Parse(rawLine);
			var root = doc.RootElement;

			if (root.TryGetProperty("event", out var eventElem))
			{
				string eventType = eventElem.GetString();

				// 1. Session initialization handshake
				if (eventType == "init")
				{
					string conversationId = root.TryGetProperty("conversation_id", out var convElem) ? convElem.GetString() : "unknown";
					GD.Print($"[AgyProcessManager] Initialized session with AGY. Conversation ID: {conversationId}");
					return;
				}

				// 2. Incremental step updates (streaming text deltas, tool status)
				if (eventType == "step_update" && root.TryGetProperty("step_update", out var stepElem))
				{
					if (stepElem.TryGetProperty("step_type", out var typeElem))
					{
						string stepType = typeElem.GetString();

						if (stepType == "agent_response" && stepElem.TryGetProperty("text_delta", out var deltaElem))
						{
							string delta = deltaElem.GetString();
							if (!string.IsNullOrEmpty(delta))
							{
								EmitChunk(delta);
							}
						}
						else if (stepType == "tool" && stepElem.TryGetProperty("tool_name", out var toolElem))
						{
							string toolName = toolElem.GetString();
							string toolState = stepElem.TryGetProperty("state", out var stateElem) ? stateElem.GetString() : "";
							GD.Print($"[AgyProcessManager] Tool executed: {toolName} ({toolState})");
						}
					}
					return;
				}

				// 3. Full turn result
				if (eventType == "result" && root.TryGetProperty("result", out var resultElem))
				{
					if (resultElem.TryGetProperty("status", out var statusElem) && statusElem.GetString() == "ERROR")
					{
						string errorMsg = resultElem.TryGetProperty("error", out var errElem) ? errElem.GetString() : "Unknown AGY error";
						GD.PrintErr($"[AGY ERROR]: {errorMsg}");
						EmitText($"[Error]: {errorMsg}");
						return;
					}

					if (resultElem.TryGetProperty("error", out var errorElem) && !string.IsNullOrEmpty(errorElem.GetString()))
					{
						string errorMsg = errorElem.GetString();
						GD.PrintErr($"[AGY ERROR]: {errorMsg}");
						EmitText($"[Error]: {errorMsg}");
						return;
					}

					if (resultElem.TryGetProperty("response", out var respElem))
					{
						string responseText = respElem.GetString();
						if (!string.IsNullOrWhiteSpace(responseText))
						{
							EmitText(responseText.Trim());
						}
					}
					return;
				}
			}
		}
		catch (JsonException)
		{
			// If raw output isn't JSON, forward directly
			EmitText(rawLine);
		}
	}

	private void EmitText(string text)
	{
		Callable.From(() =>
		{
			EmitSignal(SignalName.MessageReceived, text);
		}).CallDeferred();
	}

	private void EmitChunk(string delta)
	{
		Callable.From(() =>
		{
			EmitSignal(SignalName.ChunkReceived, delta);
		}).CallDeferred();
	}

	private void EmitTurnStarted()
	{
		Callable.From(() =>
		{
			EmitSignal(SignalName.TurnStarted);
		}).CallDeferred();
	}

	/// <summary>
	/// Writes a single JSON line to agy's stdin satisfying the printmode.StreamInputMessage schema:
	/// {"event": "user", "message": {"role": "user", "content": text}}
	/// </summary>
	public void SendJsonMessage(string action, string text)
	{
		if (_stdin == null || _agyProcess == null || _agyProcess.HasExited)
		{
			GD.PrintErr("[AgyProcessManager] Cannot send message: process is not active.");
			return;
		}

		if (string.IsNullOrWhiteSpace(text))
		{
			GD.PrintErr("[AgyProcessManager] Cannot send empty message.");
			return;
		}

		try
		{
			EmitTurnStarted();

			// Schema expected by agy stream-json:
			var payload = new
			{
				@event = "user",
				message = new
				{
					role = "user",
					content = text
				}
			};

			string jsonLine = JsonSerializer.Serialize(payload);
			_stdin.WriteLine(jsonLine);
			_stdin.Flush();

			GD.Print($"[AgyProcessManager] Sent to stdin: {jsonLine}");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[AgyProcessManager] Send error: {ex.Message}");
		}
	}

	public void SendUserMessage(string text)
	{
		SendJsonMessage("user", text);
	}

	private void TerminateAgy()
	{
		if (_agyProcess != null && !_agyProcess.HasExited)
		{
			try
			{
				_agyProcess.Kill(entireProcessTree: true);
				_agyProcess.Dispose();
				GD.Print("[AgyProcessManager] Cleaned up 'agy' process.");
			}
			catch (Exception ex)
			{
				GD.PrintErr($"[AgyProcessManager] Error terminating process: {ex.Message}");
			}
		}
	}

	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest)
		{
			TerminateAgy();
		}
	}

	public override void _ExitTree()
	{
		TerminateAgy();
	}
}
