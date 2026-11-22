# Agy Workspace & Directory Enhancement

## Problem Statement

Currently, the Godot wrapper application (`src/antigravity-office`) launches `agy` in the same working directory as the Godot process itself. Because `agy` uses its current working directory to determine the active workspace/project, `agy` always assumes it is running inside the `antigravity-office` wrapper repository rather than the user's target project.

## Current Behavior

1. **Inherited Working Directory (`AgyBridge.cs`)**
   - `AgyBridge.StartProcess()` (`src/antigravity-office/scripts/autoload/AgyBridge.cs`) spawns `stdbuf -i0 -oL -eL agy --input-format stream-json --output-format stream-json` without setting `ProcessStartInfo.WorkingDirectory`.
   - `agy --help` confirms there is no `--workspace` flag; `agy` uses its process CWD as the workspace (alongside `--project`, `--new-project`, and `--add-dir`).
2. **Immediate Auto-Start (`AgyBridge.cs`)**
   - `AgyBridge._Ready()` automatically calls `StartProcess()` on launch (unless running headless), creating a session in the wrapper directory before the user can specify a target project.
3. **No Local Command Interception (`SierraConsole.cs`)**
   - `SierraConsole.OnCommandSubmitted()` (`src/antigravity-office/scripts/ui/SierraConsole.cs`) forwards all non-empty input—including anything starting with `/`—directly to `AgyBridge.SendPrompt()`.
4. **No Restart / Reset Cleanup (`AgyBridge.cs`, `AgentDirector.cs`, `SierraConsole.cs`)**
   - `AgyBridge.ShutdownProcess()` kills the `agy` process tree, but does not emit a reset signal, reset `SierraConsole._statusBadge`, or release active `AgentLease` entries in `AgentDirector` / `AgentLeaseRegistry`.

---

## Proposed Design

### 1. `AgyBridge`: Explicit `WorkingDirectory` and Clean Workspace Switching

- Add a `WorkingDirectory` property on `AgyBridge` and assign `startInfo.WorkingDirectory = WorkingDirectory` inside `StartProcess()`.
- Add a `SessionReset` signal on `AgyBridge` emitted when the underlying `agy` session is intentionally restarted or stopped.
- Add `SwitchWorkspace(string path)`:
  1. Expand `~` and normalize with `Path.GetFullPath(path)`.
  2. Validate `Directory.Exists(resolvedPath)`; if invalid, log an error to the console without killing the active session.
  3. Call `ShutdownProcess()`.
  4. Emit `SessionReset` so:
     - `SierraConsole` resets `_statusBadge` (e.g., back to idle/ready) and logs the workspace change.
     - `AgentDirector` releases all active `AgentLease` entries in `AgentLeaseRegistry` so characters return cleanly to autonomous mode instead of remaining stuck at desks.
  5. Update `WorkingDirectory`, persist it to `user://settings.cfg`, and call `StartProcess()`.

---

### 2. Three Entry Points for Selecting a Workspace

#### A. Console Commands (`/workspace` and `/pwd`)
Intercept wrapper-local commands in `SierraConsole.OnCommandSubmitted()` before calling `AgyBridge.SendPrompt()`, while continuing to forward all other `/...` commands to `agy` (avoiding collisions with `agy`'s own slash commands such as `/init`):

- `/pwd` — Prints the current `AgyBridge.WorkingDirectory` to `_outputLog`.
- `/workspace <path>` — Calls `AgyBridge.SwitchWorkspace(path)` to close the current `agy` process and relaunch it in `<path>`. (Also supports relative paths resolved against the current `WorkingDirectory` and `~` home expansion.)

#### B. GUI Workspace Indicator & Folder Picker
- Display the active `WorkingDirectory` (or its basename with full-path tooltip) in `SierraConsole`'s header bar next to `_statusBadge`.
- Make the indicator clickable (or add a small folder button) to open a Godot `FileDialog` in `OpenDir` mode, calling `AgyBridge.SwitchWorkspace(selectedDir)` on confirmation.

#### C. Launch-Time CLI Argument & Wrapper Script
- Parse `OS.GetCmdlineUserArgs()` in `AgyBridge._Ready()` for `--workspace <path>` (and optionally `--project <name>`).
- Provide a small terminal launcher script (e.g., `agy-office [dir]`) that invokes the Godot binary with:
  ```bash
  -- --workspace "$(realpath "${1:-$PWD}")"
  ```
  Running `agy-office` from any project directory will then open `agy` directly in that directory.

---

### 3. Startup Directory Resolution & Persistence

Persist the last-used `WorkingDirectory` in `user://settings.cfg` using Godot's `ConfigFile`. On startup, `AgyBridge._Ready()` resolves the initial `WorkingDirectory` in priority order:

1. `--workspace <path>` passed via `OS.GetCmdlineUserArgs()` (if provided and valid).
2. Last-used directory saved in `user://settings.cfg` (if it still exists on disk).
3. User's home directory (`OS.GetEnvironment("HOME")`)—or defer calling `StartProcess()` until the user selects a directory—so the app never defaults to the `antigravity-office` wrapper repository.

---

## Files to Update

| File | Planned Changes |
| :--- | :--- |
| `src/antigravity-office/scripts/autoload/AgyBridge.cs` | Add `WorkingDirectory`, `SessionReset` signal, `SwitchWorkspace(string)`, `OS.GetCmdlineUserArgs()` parsing, and `user://settings.cfg` persistence. |
| `src/antigravity-office/scripts/ui/SierraConsole.cs` | Intercept `/workspace <path>` and `/pwd`, handle `SessionReset`, and add workspace indicator + `FileDialog` folder picker. |
| `src/antigravity-office/scenes/SierraConsole.tscn` | Add workspace path label/button and `FileDialog` node to the console header UI. |
| `src/antigravity-office/scripts/simulation/AgentDirector.cs` | Subscribe to `AgyBridge.SessionReset` and release active `AgentLease` entries cleanly on workspace switch. |
