# Antigravity Office — Godot 4 (.NET / C#) Architecture Specification

## 1. Overview
**Antigravity Office** is a 2D point-and-click / retro adventure office simulation built on **Godot 4 (.NET / C#)** at a base resolution of **`1376×768`** (`stretch/mode = "canvas_items"`, `stretch/aspect = "keep"`, `default_texture_filter = Nearest`).

Ten autonomous office characters inhabit five interconnected rooms—wandering, taking coffee breaks, and moving between rooms without clumping (`<= 3` characters per room). Simultaneously, a live **Antigravity CLI (`agy`)** child process streams NDJSON events into Godot, directing characters to sit at desks and type while agents are active, collaborate in the conference room when subagents spawn, and stand up to resume autonomous life when turns complete. A bottom-anchored, Sierra-style terminal overlay lets the user type prompts to `agy` and read streaming output without distorting the office scene behind it.

---

## 2. Existing Art Assets (`creative/`)

### Room Backgrounds (`1376×768` PNGs)
- `creative/google-coffee-shop-passthrough.png` — Left & right glass doors, coffee counter area.
- `creative/google-corporate-office.png` — Left & right glass doors, workspace floor.
- `creative/google-conference-room.png` — Right glass door, meeting room.
- `creative/google-printer-server-area.png` — Left corridor door, back-wall server room door, print station.
- `creative/google-server-room.png` — Left keypad door, server racks & status monitor.

### Furniture & Props (`creative/sprites/`)
- `creative/sprites/coffee-shop-counter.png` (`444×230`)
- `creative/sprites/google-developer-desk.png` (`299×215`)
- `creative/sprites/conference-table.png` (`433×150`)
- `creative/sprites/conference-chair.png` (`117×150`)
- `creative/sprites/conference-chair-back.png` (`105×150`)

### Characters (`creative/sprites/`, 10 characters × 8 sheets = 80 PNGs)
Roster: `david`, `elizabeth`, `james`, `jennifer`, `john`, `linda`, `mary`, `michael`, `patricia`, `robert`.

Each `<name>` has 8 horizontal sprite sheets (`288px` tall, feet flush on the bottom edge of each cell):
- `<name>.png` — 2-cell standing idle (front)
- `<name>-walk-down.png` — 4-cell walk down (front)
- `<name>-walk-up.png` — 4-cell walk up (back)
- `<name>-walk-right.png` — 4-cell walk right (flip `FlipH = true` in Godot for walk left)
- `<name>-drink-coffee.png` — 2-cell standing coffee sip
- `<name>-seated-front.png` — 2-cell seated facing front (no chair drawn)
- `<name>-seated-back.png` — 2-cell seated facing back (no chair drawn)
- `<name>-seated-back-typing.png` — 2-cell seated facing back typing (no chair/desk drawn)

---

## 3. `AgyBridge.cs` (Autoload `Node`) — Antigravity CLI Protocol
Uses .NET `System.Diagnostics.Process` wrapped with GNU `stdbuf` for unbuffered, real-time NDJSON stdio streaming (verified against `../antigravity-office-poc/scripts/AgyProcessManager.cs`).

### Process Launch (`stdbuf` + `PATH`)
- `FileName = "stdbuf"`
- `Arguments = "-i0 -oL -eL agy --input-format stream-json --output-format stream-json"`
  - `-i0` unbuffers `stdin`; `-oL -eL` line-buffers `stdout` and `stderr` so NDJSON lines flush immediately.
- `UseShellExecute = false`, `CreateNoWindow = true`, `RedirectStandardInput = true`, `RedirectStandardOutput = true`, `RedirectStandardError = true`.
- Appends `~/.local/bin` to `startInfo.EnvironmentVariables["PATH"]` if not already present so GUI/IDE launches locate `agy`.

### `stdin` Writer & Input Schema (`printmode.StreamInputMessage`)
- Wraps `StandardInput.BaseStream` with no UTF-8 BOM and explicit Unix newlines:
  ```csharp
  _stdinWriter = new StreamWriter(_agyProcess.StandardInput.BaseStream, new UTF8Encoding(false))
  {
      AutoFlush = true,
      NewLine = "\n"
  };
  ```
- `SendPrompt(string text)` serializes and flushes:
  ```json
  {"event": "user", "message": {"role": "user", "content": "<text>"}}
  ```

### `stdout` NDJSON Parsing & Main-Thread Signals
`OutputDataReceived` runs on a .NET thread-pool thread, parses each line with `System.Text.Json.JsonDocument.Parse(e.Data)`, and marshals C# `[Signal]` emissions onto Godot's main thread via `Callable.From(() => EmitSignal(...)).CallDeferred()`:

- **`init`** (`{"event": "init", "conversation_id": "..."}`)
  → Emits `SessionInitialized(string conversationId)`
- **`step_update`** *(fields nested under `"step_update"`)*
  - `step_type == "agent_response"` → `text_delta` → Emits `AgentTextDelta(string conversationId, string textDelta)`
  - `step_type == "tool"` → `tool_name`, `state` → Emits `ToolStepUpdated(string conversationId, string toolName, string state)`
  - Optional `subagent_info` → Emits `SubagentUpdated(...)`
- **`result`** *(fields nested under `"result"`)*
  - Extracts `status`, `error`, `response` → Emits `TurnCompleted(string conversationId, string status, string error, string response)`
- **Non-JSON fallback:** Caught via `JsonException` and emitted via `RawOutputReceived(string line)`.

### Clean Process Tree Shutdown
- Hooks `_ExitTree()`, `_Notification(int what)` for `NotificationWMCloseRequest`, and `AppDomain.CurrentDomain.ProcessExit`.
- Calls `_agyProcess.Kill(entireProcessTree: true)` (terminating both `stdbuf` and its child `agy` process) and disposes resources.

---

## 4. Non-Distorting Overlay UI (`SierraConsole.tscn` / `SierraConsole.cs`)

### Layer & Viewport Isolation
- `OfficeWorld` renders at fixed `1376×768` behind a `CanvasLayer` (`HUD`, `Layer = 10`).
- Expanding or maximizing `SierraConsole` **overlays** the room background and characters with a semi-opaque/solid retro Sierra terminal panel (`StyleBoxFlat`, e.g., `Color(0.05f, 0.07f, 0.12f, 0.93f)`) **without** resizing, squishing, or distorting `OfficeWorld` or `Camera2D`.
- Keeping `SierraConsole` in the persistent `HUD` preserves command history, scroll position, and live `agy` streaming output across room switches while visually anchoring at the bottom of every room.

### Node Hierarchy (`SierraConsole.tscn`)
```text
SierraConsole (Control - Anchors: BottomWide, CustomMinimumSize.y = 84, MouseFilter = Stop)
├── ResizeHandle (Control - Top edge, height = 8px, MouseDefaultCursorShape = Vsize)
│   └── GripBar (ColorRect - subtle pixel-art top divider/grip)
└── Frame (PanelContainer - Sierra dark-blue/slate pixel border StyleBoxFlat)
    └── MarginContainer
        └── VBoxContainer
            ├── HeaderBar (HBoxContainer)
            │   ├── StatusBadge (Label - "[AGY: READY]" / "[AGY: THINKING]" / "[TOOL: ...]")
            │   ├── Spacer (Control - SizeFlagsHorizontal = ExpandFill)
            │   ├── ClearButton (Button - "CLR")
            │   └── MaximizeButton (Button - "▲ MAX" / "▼ RESTORE")
            ├── OutputLog (RichTextLabel - BbcodeEnabled = true, ScrollFollowing = true,
            │              SelectionEnabled = true, SizeFlagsVertical = ExpandFill)
            │              # Native VScrollBar visible when content overflows
            └── InputRow (HBoxContainer)
                ├── PromptGlyph (Label - "> ")
                └── CommandInput (LineEdit - PlaceholderText = "Type a command for Antigravity...")
```

### Resizing, Maximize, & Terminal UX (`SierraConsole.cs`)
- **Drag-to-Resize:** `ResizeHandle.GuiInput` tracks left-button drag (`InputEventMouseMotion`) and adjusts `CustomMinimumSize.Y` upward (clamped between `84f` and `GetViewportRect().Size.Y * 0.90f`), storing `_restoredHeight`.
- **Maximize / Restore:** `MaximizeButton` toggles between `_restoredHeight` (compact bottom Sierra bar) and `GetViewportRect().Size.Y * 0.90f` (expanded reading view covering the office).
- **Streaming & History:**
  - `CommandInput.TextSubmitted` echoes `[color=#ffcc44]> {text}[/color]`, appends to `_history` (navigable via `Up`/`Down` arrows), and calls `AgyBridge.SendPrompt(text)`.
  - `AgentTextDelta` appends streaming text inline; `ToolStepUpdated` logs `[color=#77cc88]⚙ {toolName} ({state})[/color]`; `TurnCompleted` logs errors (`status == "ERROR"`) in red or a subtle turn-complete divider.
  - `Escape` releases focus from `CommandInput` for room/camera navigation; `/` or `Enter` re-focuses it.

---

## 5. Simulation & Office Integration

### `AgentDirector.cs`
- Subscribes to `AgyBridge` signals and manages `Dictionary<string, AgentLease> _leases` (`conversation_id` → character, target room/POI, grace timer) alongside `Queue<string> _availableCharacters` (the 10-character roster).
- **Event-to-Behavior Mapping:**
  - **Turn / Agent Start (`SessionInitialized` / `AgentTextDelta` / `ToolStepUpdated`):** Leases a character, directs them to `corporate_office` → `DeskPOI`, sits (`seated_back`), and plays `seated_back_typing`.
  - **Subagent Spawn (`SubagentUpdated`):** Leases an additional idle character to a secondary `corporate_office` `DeskPOI` (`seated_back_typing`) or `conference_room` `ConferenceSeatPOI` (`seated_front`/`seated_back`); releases on `DONE`.
  - **Turn Completed (`TurnCompleted`):** Switches the main character from `seated_back_typing` to `seated_back` and starts a `12s` `WAITING_INPUT` grace timer. A follow-up prompt cancels the timer and resumes typing; timer expiration stands the character up (`idle`), releases the desk, and returns them to `Autonomous` mode.

### `RoomManager.cs` (Autoload `Node`)
- **Room Graph & BFS Routing:** Models the 5 rooms, `DoorPortal` waypoints, and `FindRoomPath(fromRoom, toRoom)` via breadth-first search.
- **Room Topology (Left to Right):**
  ```text
  [Conference Room] <--(Right / Left)--> [Corporate Office] <--(Right / Left)--> [Coffee Shop] <--(Right / Left)--> [Hallway / Printer Area]
                                                                                                                             ^
                                                                                                                             | (Top / Left)
                                                                                                                             v
                                                                                                                       [Server Room]
  ```
  - `conference_room` (`DoorRight`) ↔ `corporate_office` (`DoorLeft`)
  - `corporate_office` (`DoorRight`) ↔ `coffee_shop` (`DoorLeft`)
  - `coffee_shop` (`DoorRight`) ↔ `printer_server_area` (`DoorLeft`)
  - `printer_server_area` (`DoorTop` / back-wall server door) ↔ `server_room` (`DoorLeft`)
- **Per-Room Occupancy Cap (`MaxCapacity = 3`):**
  - Tracks `occupants` + `incomingReservations` per room.
  - **Autonomous requests** are denied if `occupants + incomingReservations >= 3`.
  - **Directed requests (Priority Eviction):** If the target room is full (`3`), `RoomManager` evicts one `Autonomous` occupant/reservation to an open room (e.g., `coffee_shop` or `printer_server_area`) and reserves the freed slot for the `Directed` agent. If all 3 slots are held by `Directed` agents, `AgentDirector` overflows to `conference_room`.
- **Door Cooldowns:** Enforces a `2.0–4.0s` cooldown per portal so characters enter and exit naturally without clumping.
- **Initial Random Seeding:** On startup, the 10 characters are randomly distributed across the 5 rooms while respecting `MaxCapacity = 3` per room, and can freely pass to and from rooms through connected `DoorPortal`s.

### `CharacterAgent.cs` (`CharacterBody2D` / `Node2D`)
- **Dual-Mode FSM:** `Mode.Autonomous` vs `Mode.Directed`.
  - `Autonomous` characters meander within rooms (`NavigationRegion2D` + `NavigationAgent2D` with `avoidance_enabled = true`), stop at `coffee_shop` (`drink_coffee`), sit in `conference_room`, or inspect `server_room` / `printer_server_area`.
  - `Directed` characters immediately cancel autonomous timers/animations (standing up cleanly if seated), traverse `RoomManager` BFS portals, and play their assigned `POIMarker` animation.
- **Y-Sorting & Sprite Anchoring:**
  - All 10 characters and furniture props live under `YSortActors` (`YSortEnabled = true`) with bottom-center origins (`AnimatedSprite2D.Offset.Y = -144` with `Centered = true`), locking feet to `(0, 0)` across varying cell widths.
  - Seated POIs (`DeskPOI`, `ConferenceSeatPOI`) use slight Y-offsets relative to `google-developer-desk.png`, `conference-chair.png`, and `conference-chair-back.png` so characters render cleanly behind desks and on chairs.

---

## 6. Scene Tree (`Main.tscn`)

```text
Main (Node)
├── Autoloads (Project Settings)
│   ├── AgyBridge.cs              # stdbuf + agy stream-json, StreamWriter(UTF8NoBOM), CallDeferred signals, Kill(entireProcessTree: true)
│   └── RoomManager.cs            # 5-room BFS graph, POIMarker registry, MaxCapacity=3 + autonomous eviction
├── AgentDirector.cs              # AgyBridge signals <-> character leases, DeskPOI/ConferencePOI, 12s grace timer
├── OfficeWorld (Node2D)          # Fixed 1376x768 world (never squished/resized by HUD)
│   ├── Rooms (Node2D)
│   │   ├── CoffeeShop            # DEFAULT / starting visible room (google-coffee-shop-passthrough.png + CounterPOI + Portals)
│   │   ├── CorporateOffice       # google-corporate-office.png + 3x DeskPOIs + Portals
│   │   ├── ConferenceRoom        # google-conference-room.png + Table/SeatPOIs + Portals
│   │   ├── PrinterServerArea     # google-printer-server-area.png + PrinterPOIs + Portals
│   │   └── ServerRoom            # google-server-room.png + ServerRackPOIs + Portals
│   ├── YSortActors (Node2D)      # YSortEnabled = true
│   │   └── [10x CharacterAgent.tscn (CharacterAgent.cs)]
│   └── Camera2D                  # Room pan/zoom
└── HUD (CanvasLayer, Layer = 10)
    └── SierraConsole.tscn        # Bottom-anchored overlay, drag-resizable, maximizable, RichTextLabel + VScrollBar + LineEdit
```
