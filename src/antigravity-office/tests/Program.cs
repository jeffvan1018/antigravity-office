using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AntigravityOffice;

int passed = 0;
int failed = 0;

void Assert(bool condition, string message)
{
    if (condition)
    {
        passed++;
    }
    else
    {
        failed++;
        Console.Error.WriteLine($"FAIL: {message}");
    }
}

// 1. AgyBridge NDJSON Serialization & Parsing Tests
{
    string json = AgyBridge.SerializePrompt("Hello \"world\"\nLine 2");
    using var doc = JsonDocument.Parse(json);
    Assert(doc.RootElement.GetProperty("event").GetString() == "user", "SerializePrompt event == user");
    Assert(doc.RootElement.GetProperty("message").GetProperty("role").GetString() == "user", "SerializePrompt role == user");
    Assert(doc.RootElement.GetProperty("message").GetProperty("content").GetString() == "Hello \"world\"\nLine 2", "SerializePrompt content preserved");

    Assert(AgyBridge.ParseNdjsonLine(null).Count == 0, "ParseNdjsonLine null -> empty");
    Assert(AgyBridge.ParseNdjsonLine("   ").Count == 0, "ParseNdjsonLine whitespace -> empty");

    var rawEvents = AgyBridge.ParseNdjsonLine("not valid json {");
    Assert(rawEvents.Count == 1 && rawEvents[0].Kind == AgyEventKind.RawOutput && rawEvents[0].RawLine == "not valid json {", "Malformed JSON -> RawOutput");

    var initEvents = AgyBridge.ParseNdjsonLine("{\"event\":\"init\",\"conversation_id\":\"conv-123\"}");
    Assert(initEvents.Count == 1 && initEvents[0].Kind == AgyEventKind.Init && initEvents[0].ConversationId == "conv-123", "Init event parsed");

    var deltaEvents = AgyBridge.ParseNdjsonLine("{\"event\":\"step_update\",\"conversation_id\":\"conv-123\",\"step_update\":{\"step_type\":\"agent_response\",\"text_delta\":\"Hi!\"}}");
    Assert(deltaEvents.Count == 1 && deltaEvents[0].Kind == AgyEventKind.AgentResponse && deltaEvents[0].TextDelta == "Hi!", "AgentResponse delta parsed");

    var toolEvents = AgyBridge.ParseNdjsonLine("{\"event\":\"step_update\",\"conversation_id\":\"conv-123\",\"step_update\":{\"step_type\":\"tool\",\"tool_name\":\"run_command\",\"state\":\"RUNNING\"}}");
    Assert(toolEvents.Count == 1 && toolEvents[0].Kind == AgyEventKind.ToolStep && toolEvents[0].ToolName == "run_command" && toolEvents[0].ToolState == "RUNNING", "ToolStep parsed");

    var subEvents = AgyBridge.ParseNdjsonLine("{\"event\":\"step_update\",\"conversation_id\":\"conv-123\",\"step_update\":{\"step_type\":\"tool\",\"tool_name\":\"delegate\",\"state\":\"RUNNING\",\"subagent_info\":{\"subagent_id\":\"sub-1\",\"name\":\"worker\",\"state\":\"RUNNING\"}}}");
    Assert(subEvents.Count == 2 && subEvents[1].Kind == AgyEventKind.SubagentUpdate && subEvents[1].SubagentId == "sub-1" && subEvents[1].SubagentName == "worker", "SubagentInfo parsed");

    var resultEvents = AgyBridge.ParseNdjsonLine("{\"event\":\"result\",\"conversation_id\":\"conv-123\",\"result\":{\"status\":\"ERROR\",\"error\":\"boom\",\"response\":\"\"}}");
    Assert(resultEvents.Count == 1 && resultEvents[0].Kind == AgyEventKind.Result && resultEvents[0].Status == "ERROR" && resultEvents[0].Error == "boom", "Result error parsed");
}

// 2. RoomGraphState BFS, Capacity, Eviction, Cooldowns, Seeding Tests
{
    var graph = new RoomGraphState();

    var path = graph.FindRoomPath("conference_room", "server_room");
    Assert(path.Count == 4, "BFS conference_room -> server_room has 4 hops");
    Assert(path[0].FromPortal == "DoorRight" && path[0].ToRoom == "corporate_office", "Hop 0 -> corporate_office");
    Assert(path[3].FromRoom == "printer_server_area" && path[3].FromPortal == "DoorTop" && path[3].ToRoom == "server_room", "Hop 3 -> server_room via DoorTop");

    Assert(graph.FindRoomPath("coffee_shop", "coffee_shop").Count == 0, "Same-room path is empty");
    Assert(graph.FindRoomPath("coffee_shop", "unknown_room").Count == 0, "Unknown room path is empty");

    var seeds = graph.SeedCharacters(CharacterAgent.Roster, new Random(42));
    Assert(seeds.Count == 10, "All 10 characters seeded");
    foreach (string room in RoomGraphState.AllRooms)
    {
        Assert(graph.GetEffectiveOccupancy(room) <= RoomGraphState.MaxCapacity, $"Seeded room {room} <= MaxCapacity (3)");
    }

    // Fill corporate_office with 3 autonomous characters
    var g2 = new RoomGraphState();
    g2.PlaceCharacterInRoom("david", "corporate_office");
    g2.PlaceCharacterInRoom("elizabeth", "corporate_office");
    g2.PlaceCharacterInRoom("james", "corporate_office");
    g2.PlaceCharacterInRoom("jennifer", "coffee_shop");

    Assert(!g2.RequestAutonomousMove("jennifer", "coffee_shop", "corporate_office"), "Autonomous move denied when target room at MaxCapacity=3");

    // Directed move evicts one autonomous occupant
    Assert(g2.RequestDirectedMove("jennifer", "coffee_shop", "corporate_office", out EvictionNotice? eviction), "Directed move succeeds via eviction");
    Assert(eviction is not null && eviction.TargetRoom != "corporate_office", "EvictionNotice targets open room");
    Assert(g2.GetEffectiveOccupancy("corporate_office") == 3, "corporate_office remains at capacity 3 after eviction");

    // Fill all 3 slots with Directed agents -> 4th Directed agent returns false
    g2.SetDirectedMode("david", true);
    g2.SetDirectedMode("elizabeth", true);
    g2.SetDirectedMode("james", true);
    g2.PlaceCharacterInRoom("david", "corporate_office");
    g2.PlaceCharacterInRoom("elizabeth", "corporate_office");
    g2.PlaceCharacterInRoom("james", "corporate_office");
    g2.PlaceCharacterInRoom("robert", "coffee_shop");

    Assert(!g2.RequestDirectedMove("robert", "coffee_shop", "corporate_office", out EvictionNotice? noEviction) && noEviction is null,
        "Directed move returns false when all 3 slots held by Directed agents");

    // Portal cooldowns
    g2.TriggerPortalCooldown("coffee_shop", "DoorLeft", 10.0, 1.0); // clamped to 2.0s
    Assert(g2.IsPortalOnCooldown("coffee_shop", "DoorLeft", 11.5), "Portal still on cooldown at 11.5s");
    Assert(!g2.IsPortalOnCooldown("coffee_shop", "DoorLeft", 12.1), "Portal cooldown expired at 12.1s");

    // Evicting a reservation whose occupant is already in the chosen openRoom must not double-count occupancy
    var g3 = new RoomGraphState();
    g3.PlaceCharacterInRoom("david", "coffee_shop");
    g3.PlaceCharacterInRoom("elizabeth", "corporate_office");
    g3.PlaceCharacterInRoom("james", "corporate_office");
    g3.SetDirectedMode("elizabeth", true);
    g3.SetDirectedMode("james", true);
    Assert(g3.RequestAutonomousMove("david", "coffee_shop", "corporate_office"), "David reserves 3rd slot in corporate_office");
    g3.PlaceCharacterInRoom("jennifer", "printer_server_area");
    Assert(g3.RequestDirectedMove("jennifer", "printer_server_area", "corporate_office", out EvictionNotice? resEviction),
        "Directed jennifer evicts david's reservation");
    Assert(resEviction is not null && resEviction.EvictedCharacter == "david" && resEviction.TargetRoom == "coffee_shop",
        "Evicted reservation returns to coffee_shop");
    Assert(g3.GetEffectiveOccupancy("coffee_shop") == 1,
        "coffee_shop occupancy is 1 (not double-counted as occupant + reservation)");

    // Same-room move cancels any stale reservation in another room
    g3.PlaceCharacterInRoom("robert", "printer_server_area");
    Assert(g3.RequestAutonomousMove("robert", "printer_server_area", "server_room"), "Robert reserves server_room");
    Assert(g3.GetEffectiveOccupancy("server_room") == 1, "server_room has 1 reservation");
    Assert(g3.RequestAutonomousMove("robert", "printer_server_area", "printer_server_area"), "Same-room move succeeds");
    Assert(g3.GetEffectiveOccupancy("server_room") == 0, "Stale reservation in server_room cleared on same-room move");
}

// 3. AgentLeaseRegistry Tests (Leases, Overflow, 12s Grace Timer, Subagents)
{
    var registry = new AgentLeaseRegistry(CharacterAgent.Roster);
    int corpDirectedCount = 0;

    bool ReserveRoom(string _charName, string room)
    {
        if (room == "corporate_office")
        {
            if (corpDirectedCount >= 3)
            {
                return false;
            }
            corpDirectedCount++;
            return true;
        }
        return true;
    }

    var lease1 = registry.AcquireOrResumeMainLease("c1", ReserveRoom);
    Assert(lease1 is not null && lease1.TargetRoom == "corporate_office" && lease1.TargetPoiKind == "DeskPOI", "Lease 1 assigned to corporate_office DeskPOI");

    var lease2 = registry.AcquireOrResumeMainLease("c2", ReserveRoom);
    var lease3 = registry.AcquireOrResumeMainLease("c3", ReserveRoom);
    var lease4 = registry.AcquireOrResumeMainLease("c4", ReserveRoom);
    Assert(lease2 is not null && lease3 is not null, "Leases 2 & 3 assigned");
    Assert(lease4 is not null && lease4.TargetRoom == "conference_room" && lease4.TargetPoiKind == "ConferenceSeatPOI", "4th Directed agent overflows to conference_room");

    // Grace period 12s
    registry.BeginGracePeriod("c1");
    Assert(registry.TickGraceTimers(11.0).Count == 0, "Grace timer not expired at 11s");

    // Follow-up prompt cancels grace timer
    registry.AcquireOrResumeMainLease("c1", ReserveRoom);
    Assert(lease1!.State == LeaseState.ActiveTyping, "Follow-up prompt resumes ActiveTyping");
    Assert(registry.TickGraceTimers(15.0).Count == 0, "Canceled grace timer does not expire");

    // Complete again and let 12s expire
    registry.BeginGracePeriod("c1");
    var expired = registry.TickGraceTimers(12.1);
    Assert(expired.Count == 1 && expired[0].CharacterName == lease1.CharacterName, "Grace timer expires after 12s");

    // Subagent lease & release
    var subLease = registry.AcquireSubagentLease("c2", "sub-99", ReserveRoom);
    Assert(subLease is not null && subLease.CharacterName != lease2!.CharacterName, "Subagent gets distinct character");
    var releasedSub = registry.ReleaseSubagentLease("c2", "sub-99");
    Assert(releasedSub is not null && releasedSub.CharacterName == subLease!.CharacterName, "Subagent lease released cleanly");

    // Both corporate_office and conference_room full -> returns null without leaking character
    int beforeAvail = registry.AvailableCount;
    var deniedLease = registry.AcquireOrResumeMainLease("c-denied", (_, _) => false);
    Assert(deniedLease is null && registry.AvailableCount == beforeAvail,
        "Lease returns null and preserves AvailableCount when both corporate_office and conference_room are full");
}

// 4. Room Walk Bounds Tests (ServerRoom moved-down vertical navigation border)
{
    var serverBounds = new Godot.Rect2(6400f, 0f, 1376f, 768f);
    var serverWalk = RoomManager.ComputeDefaultWalkBounds("server_room", serverBounds);
    Assert(serverWalk.Position.Y >= 580f, "Default server_room walk minY is >= 580");
    Assert(serverWalk.End.Y == 698f, "Default server_room walk maxY is 698");

    var corpBounds = new Godot.Rect2(1600f, 0f, 1376f, 768f);
    var corpWalk = RoomManager.ComputeDefaultWalkBounds("corporate_office", corpBounds);
    Assert(corpWalk.Position.Y == 460f, "Default corporate_office walk minY remains 460");

    var serverPolyVerts = new[]
    {
        new Godot.Vector2(1246f, 580f),
        new Godot.Vector2(1246f, 710f),
        new Godot.Vector2(130f, 710f),
        new Godot.Vector2(130f, 580f),
    };
    var polyWalk = RoomManager.ComputeWalkBoundsFromPolygon(serverPolyVerts, new Godot.Vector2(6400f, 0f));
    Assert(polyWalk.Position.X == 6580f && polyWalk.End.X == 7596f, "Polygon walk X bounds padded inside server room");
    Assert(polyWalk.Position.Y == 580f && polyWalk.End.Y == 698f, "Polygon walk Y bounds respect 580..698");

    var emptyWalk = RoomManager.ComputeWalkBoundsFromPolygon(Array.Empty<Godot.Vector2>(), Godot.Vector2.Zero);
    Assert(emptyWalk.Size == Godot.Vector2.Zero, "Empty polygon returns zero-sized Rect2");
}

// 5. Furniture Obstacle Avoidance & Random Walk Point Tests
{
    var corpWalk = new Godot.Rect2(1780f, 470f, 1016f, 228f);
    var deskObstacles = new[]
    {
        new Godot.Rect2(1815f, 475f, 270f, 52f),
        new Godot.Rect2(2153f, 475f, 270f, 52f),
        new Godot.Rect2(2491f, 475f, 270f, 52f),
    };

    Assert(RoomManager.IsPointBlockedByObstacles(new Godot.Vector2(1950f, 500f), deskObstacles),
        "Point inside Desk1 is blocked");
    Assert(RoomManager.IsPointBlockedForAgent(new Godot.Vector2(1950f, 550f), deskObstacles),
        "Point at (1950, 550) below Desk1 is blocked for agent due to 28px upward collision shape");
    Assert(!RoomManager.IsPointBlockedForAgent(new Godot.Vector2(1950f, 558f), deskObstacles),
        "DeskPOI1 at (1950, 558) is clear of Desk1 obstacle for agent");

    var rng = new Random(123);
    bool anyInsideObstacle = false;
    for (int i = 0; i < 200; i++)
    {
        Godot.Vector2 pt = RoomManager.PickWalkPointInBounds(corpWalk, deskObstacles, rng);
        if (RoomManager.IsPointBlockedForAgent(pt, deskObstacles) ||
            RoomManager.IsPointBlockedByObstacles(pt, deskObstacles, padding: 20f))
        {
            anyInsideObstacle = true;
            break;
        }
    }
    Assert(!anyInsideObstacle, "200 random walk points in corporate_office all avoid desk obstacles with agent clearance");

    // Conference table & POIs clearance
    var confObstacles = new[] { new Godot.Rect2(480.5f, 486f, 415f, 46f) };
    Assert(RoomManager.IsPointBlockedForAgent(new Godot.Vector2(688f, 510f), confObstacles),
        "Center of ConfTable is blocked for agent");
    Assert(RoomManager.IsPointBlockedForAgent(new Godot.Vector2(688f, 555f), confObstacles),
        "Point at (688, 555) below ConfTable is blocked for agent");
    Assert(!RoomManager.IsPointBlockedForAgent(new Godot.Vector2(560f, 472f), confObstacles),
        "ConferenceSeatPOI1 (560, 472) is clear of ConfTable for agent");
    Assert(!RoomManager.IsPointBlockedForAgent(new Godot.Vector2(560f, 563f), confObstacles),
        "ConferenceSeatPOI3 (560, 563) is clear of ConfTable for agent");

    // Coffee counter & POIs clearance
    var coffeeObstacles = new[] { new Godot.Rect2(3673f, 465f, 430f, 55f) };
    Assert(RoomManager.IsPointBlockedForAgent(new Godot.Vector2(3888f, 490f), coffeeObstacles),
        "Center of CoffeeCounter is blocked for agent");
    Assert(!RoomManager.IsPointBlockedForAgent(new Godot.Vector2(3740f, 560f), coffeeObstacles),
        "CounterPOI1 (3740, 560) is clear of CoffeeCounter for agent");

    // Fallback when entire walkBounds is covered by an obstacle
    var coveredWalk = new Godot.Rect2(500f, 490f, 100f, 40f);
    Godot.Vector2 fallbackPt = RoomManager.PickWalkPointInBounds(coveredWalk, confObstacles, rng);
    Assert(fallbackPt != Godot.Vector2.Zero, "PickWalkPointInBounds returns deterministic fallback when all samples blocked");

    // PrinterServerArea walk bounds, printer cutout obstacle, POIs & portals
    var printerAreaBounds = new Godot.Rect2(4800f, 0f, 1376f, 768f);
    var printerDefaultWalk = RoomManager.ComputeDefaultWalkBounds("printer_server_area", printerAreaBounds);
    Assert(printerDefaultWalk.Position.Y >= 575f, "Default printer_server_area walk minY is >= 575 (off back wall)");

    var printerPolyVerts = new[]
    {
        new Godot.Vector2(1170f, 575f),
        new Godot.Vector2(1170f, 710f),
        new Godot.Vector2(130f, 710f),
        new Godot.Vector2(130f, 575f),
    };
    var printerWalk = RoomManager.ComputeWalkBoundsFromPolygon(printerPolyVerts, new Godot.Vector2(4800f, 0f));
    Assert(printerWalk.Position.X == 4980f && printerWalk.End.X == 5920f, "PrinterServerArea walk X bounds stay clear of left/right walls");
    Assert(printerWalk.Position.Y == 575f && printerWalk.End.Y == 698f, "PrinterServerArea walk Y bounds stay on floor (575..698)");

    var printerObstacles = new[] { new Godot.Rect2(5125f, 555f, 265f, 39f) };
    Assert(RoomManager.IsPointBlockedForAgent(new Godot.Vector2(5257f, 575f), printerObstacles),
        "Center of printer is blocked for agent");
    Assert(RoomManager.IsPointBlockedForAgent(new Godot.Vector2(5257f, 610f), printerObstacles),
        "Point at (5257, 610) immediately in front of printer is blocked for agent due to 28px upward collision shape");
    Assert(!RoomManager.IsPointBlockedForAgent(new Godot.Vector2(5275f, 636f), printerObstacles),
        "PrinterPOI1 (475, 636) is clear of printer obstacle for agent");
    Assert(!RoomManager.IsPointBlockedForAgent(new Godot.Vector2(5445f, 618f), printerObstacles),
        "PrinterPOI2 (645, 618) is clear of printer obstacle for agent");
    Assert(!RoomManager.IsPointBlockedForAgent(new Godot.Vector2(5740f, 580f), printerObstacles),
        "PrinterServerArea DoorTop (940, 580) is on floor and clear of printer obstacle");
    Assert(!RoomManager.IsPointBlockedForAgent(new Godot.Vector2(4936f, 585f), printerObstacles),
        "PrinterServerArea DoorLeft (136, 585) is on floor and clear of printer obstacle");

    bool anyPrinterHit = false;
    for (int i = 0; i < 200; i++)
    {
        Godot.Vector2 pt = RoomManager.PickWalkPointInBounds(printerWalk, printerObstacles, rng);
        if (pt.Y < 575f ||
            pt.X > 5920f ||
            RoomManager.IsPointBlockedForAgent(pt, printerObstacles) ||
            RoomManager.IsPointBlockedByObstacles(pt, printerObstacles, padding: 20f))
        {
            anyPrinterHit = true;
            break;
        }
    }
    Assert(!anyPrinterHit, "200 random walk points in printer_server_area stay off walls and avoid printer cutout");
}

// 6. StuckTracker, Sidestep Steering, Occupied-Aware Walk Points & POI Switching Tests
{
    // StuckTracker tests
    var tracker = new StuckTracker();
    tracker.Reset(new Godot.Vector2(100f, 100f));
    tracker.Update(new Godot.Vector2(102f, 101f), 0.4); // < 6px progress -> sidestepping
    Assert(tracker.IsSidestepping && !tracker.IsStuck, "StuckTracker enters sidestepping after 0.4s without >=6px progress");

    tracker.Update(new Godot.Vector2(110f, 100f), 0.1); // >= 6px progress -> resets
    Assert(!tracker.IsSidestepping && !tracker.IsStuck, "StuckTracker resets when agent makes >=6px progress");

    // Oscillating within <6px still triggers stuck
    for (int i = 0; i < 10; i++)
    {
        float jitterX = (i % 2 == 0) ? 112f : 109f;
        tracker.Update(new Godot.Vector2(jitterX, 100f), 0.2);
    }
    Assert(tracker.IsStuck, "StuckTracker detects oscillation deadlock (<6px net progress over 2.0s)");

    tracker.MarkRecoveryAttempt(new Godot.Vector2(110f, 100f));
    Assert(tracker.RecoveryCount == 1 && !tracker.IsStuck, "MarkRecoveryAttempt increments RecoveryCount and clears stuck timer");
    tracker.Reset(new Godot.Vector2(110f, 100f), resetRecoveryCount: true);
    Assert(tracker.RecoveryCount == 0, "Reset with resetRecoveryCount=true clears RecoveryCount");

    // ComputeSteeredDirection tests (head-on symmetry breaking)
    Godot.Vector2 dirRight = CharacterAgent.ComputeSteeredDirection(
        new Godot.Vector2(100f, 500f),
        new Godot.Vector2(1f, 0f),
        new[] { new Godot.Vector2(130f, 500f) },
        forceSidestep: false);
    Godot.Vector2 dirLeft = CharacterAgent.ComputeSteeredDirection(
        new Godot.Vector2(130f, 500f),
        new Godot.Vector2(-1f, 0f),
        new[] { new Godot.Vector2(100f, 500f) },
        forceSidestep: false);
    Assert(dirRight.Y > 0.2f && dirLeft.Y < -0.2f,
        "Head-on agents steer in opposite Y directions (right-hand rule) to pass around each other");

    // Agent behind does not trigger sidestep
    Godot.Vector2 clearDir = CharacterAgent.ComputeSteeredDirection(
        new Godot.Vector2(100f, 500f),
        new Godot.Vector2(1f, 0f),
        new[] { new Godot.Vector2(70f, 500f) },
        forceSidestep: false);
    Assert(Godot.Mathf.Abs(clearDir.Y) < 0.001f && clearDir.X > 0.999f,
        "Agent behind does not deflect forward direction");

    // Blocking agent to the right steers left (-right)
    Godot.Vector2 steerLeftAround = CharacterAgent.ComputeSteeredDirection(
        new Godot.Vector2(100f, 500f),
        new Godot.Vector2(1f, 0f),
        new[] { new Godot.Vector2(125f, 520f) },
        forceSidestep: false);
    Assert(steerLeftAround.Y < -0.2f, "Agent blocking on right side causes left sidestep");

    // Occupied-aware walk point selection
    var roomWalk = new Godot.Rect2(3380f, 470f, 1016f, 228f);
    var occupied = new[]
    {
        new Godot.Vector2(3740f, 560f),
        new Godot.Vector2(4036f, 560f),
    };
    var rng6 = new Random(77);
    bool anyTooClose = false;
    for (int i = 0; i < 100; i++)
    {
        Godot.Vector2 pt = RoomManager.PickWalkPointInBounds(
            roomWalk,
            Array.Empty<Godot.Rect2>(),
            rng6,
            occupiedPositions: occupied,
            agentClearance: 44f);
        if (!RoomManager.IsPointClearOfAgents(pt, occupied, 44f))
        {
            anyTooClose = true;
            break;
        }
    }
    Assert(!anyTooClose, "100 walk points all maintain >=44px clearance from occupied agent positions");

    // Fallback in crowded small bounds picks farthest obstacle-free point
    var tinyBounds = new Godot.Rect2(0f, 0f, 60f, 60f);
    var crowd = new[] { new Godot.Vector2(10f, 10f) };
    Godot.Vector2 bestPt = RoomManager.PickWalkPointInBounds(
        tinyBounds,
        Array.Empty<Godot.Rect2>(),
        rng6,
        occupiedPositions: crowd,
        agentClearance: 200f);
    Assert(bestPt.DistanceTo(crowd[0]) > 10f, "Crowded fallback picks a point away from the blocking agent");

    // Least-crowded POI selection (pure C# without Godot Node instantiation)
    var poiPositions = new[]
    {
        new Godot.Vector2(100f, 500f),
        new Godot.Vector2(400f, 500f),
    };
    int chosenIdx = RoomManager.SelectBestPoiIndex(
        poiPositions,
        new[] { new Godot.Vector2(100f, 500f) },
        rng6);
    Assert(chosenIdx == 1, "SelectBestPoiIndex prefers uncrowded POI over crowded POI");

    // When all POIs are crowded, picks the POI farthest from the nearest occupied agent
    var crowdedPois = new[]
    {
        new Godot.Vector2(100f, 500f),
        new Godot.Vector2(125f, 500f),
    };
    int crowdedBestIdx = RoomManager.SelectBestPoiIndex(
        crowdedPois,
        new[] { new Godot.Vector2(102f, 500f) },
        rng6);
    Assert(crowdedBestIdx == 1,
        "SelectBestPoiIndex picks least-crowded POI when all POIs are within clearance");

    // Boundary-aware ComputeSteeredDirection avoids steering off top of ServerRoom walkBounds
    var serverWalkBounds = new Godot.Rect2(6580f, 580f, 1016f, 118f);
    Godot.Vector2 nearTopWallDir = CharacterAgent.ComputeSteeredDirection(
        new Godot.Vector2(6900f, 590f),
        new Godot.Vector2(-1f, 0f),
        new[] { new Godot.Vector2(6870f, 590f) },
        forceSidestep: false,
        walkBounds: serverWalkBounds);
    Assert(nearTopWallDir.Y > 0.2f,
        "Agent walking left near top boundary steers down into room instead of out of walkBounds");

    // preferLeftSidestep flips default sidestep direction
    Godot.Vector2 flippedDir = CharacterAgent.ComputeSteeredDirection(
        new Godot.Vector2(100f, 500f),
        new Godot.Vector2(1f, 0f),
        new[] { new Godot.Vector2(130f, 500f) },
        forceSidestep: true,
        preferLeftSidestep: true);
    Assert(flippedDir.Y < -0.2f,
        "preferLeftSidestep flips sidestep direction to left (-right)");
}

// 7. Screen Resizing Cover-Fill Camera Zoom Tests
{
    Godot.Vector2 exact16x9 = Main.ComputeCoverZoom(new Godot.Vector2(1376f, 768f));
    Assert(Godot.Mathf.IsEqualApprox(exact16x9.X, 1f) && Godot.Mathf.IsEqualApprox(exact16x9.Y, 1f),
        "16:9 base viewport (1376x768) uses zoom (1, 1)");

    // Ultrawide (e.g. 21:9 -> virtual viewport 1820.44 x 768): zoom >= 1820.44 / 1376 so visible width == 1376
    float ultrawideWidth = 768f * (2560f / 1080f);
    Godot.Vector2 ultrawideZoom = Main.ComputeCoverZoom(new Godot.Vector2(ultrawideWidth, 768f));
    float visibleWidth = ultrawideWidth / ultrawideZoom.X;
    float visibleHeight = 768f / ultrawideZoom.Y;
    Assert(Godot.Mathf.IsEqualApprox(ultrawideZoom.X, ultrawideZoom.Y) &&
           Godot.Mathf.IsEqualApprox(visibleWidth, 1376f, 0.01f) &&
           visibleHeight <= 768f + 0.01f,
        "Ultrawide viewport zooms uniformly to fill 1376px room width without exposing horizontal gaps");

    // Tall / 1:1 (virtual viewport 1376 x 1376): zoom == 1376 / 768 so visible height == 768
    Godot.Vector2 squareZoom = Main.ComputeCoverZoom(new Godot.Vector2(1376f, 1376f));
    float sqVisibleWidth = 1376f / squareZoom.X;
    float sqVisibleHeight = 1376f / squareZoom.Y;
    Assert(Godot.Mathf.IsEqualApprox(sqVisibleHeight, 768f, 0.01f) &&
           sqVisibleWidth <= 1376f + 0.01f,
        "Tall/square viewport zooms uniformly to fill 768px room height without exposing vertical void");

    // Degenerate zero, negative, NaN, or infinite viewport sizes fall back safely to (1, 1)
    Assert(Main.ComputeCoverZoom(Godot.Vector2.Zero) == Godot.Vector2.One,
        "Zero viewport size falls back to zoom (1, 1)");
    Assert(Main.ComputeCoverZoom(new Godot.Vector2(-100f, 768f)) == Godot.Vector2.One,
        "Negative viewport width falls back to zoom (1, 1)");
    Assert(Main.ComputeCoverZoom(new Godot.Vector2(float.NaN, 768f)) == Godot.Vector2.One,
        "NaN viewport width falls back to zoom (1, 1)");
    Assert(Main.ComputeCoverZoom(new Godot.Vector2(1376f, float.PositiveInfinity)) == Godot.Vector2.One,
        "Infinite viewport height falls back to zoom (1, 1)");
}

// 8. Agy Workspace Resolution, CLI Args, Local Commands, and Lease Reset Tests
{
    string home = "/Users/tester";
    string currentBase = "/Users/tester/projects/alpha";

    Assert(AgyBridge.ResolveWorkspacePath("~", currentBase, home) == "/Users/tester",
        "ResolveWorkspacePath expands '~' to home directory");
    Assert(AgyBridge.ResolveWorkspacePath("~/work/repo/", currentBase, home) == "/Users/tester/work/repo",
        "ResolveWorkspacePath expands '~/...' and trims trailing slash");
    Assert(AgyBridge.ResolveWorkspacePath("../beta", currentBase, home) == "/Users/tester/projects/beta",
        "ResolveWorkspacePath resolves relative paths against base directory");
    Assert(AgyBridge.ResolveWorkspacePath("\"/tmp/my dir\"", currentBase, home) == "/tmp/my dir",
        "ResolveWorkspacePath strips surrounding quotes");
    Assert(AgyBridge.ResolveWorkspacePath("   ", currentBase, home) == string.Empty,
        "ResolveWorkspacePath returns empty for whitespace");

    var (ws1, proj1) = AgyBridge.ParseLaunchArgs(new[] { "--workspace", "/tmp/ws1", "--project", "demo" });
    Assert(ws1 == "/tmp/ws1" && proj1 == "demo", "ParseLaunchArgs parses space-separated --workspace and --project");

    var (ws2, proj2) = AgyBridge.ParseLaunchArgs(new[] { "--workspace=/tmp/ws2", "--project=app2" });
    Assert(ws2 == "/tmp/ws2" && proj2 == "app2", "ParseLaunchArgs parses equals-separated --workspace and --project");

    var (ws3, proj3) = AgyBridge.ParseLaunchArgs(new[] { "--workspace" });
    Assert(ws3 is null && proj3 is null, "ParseLaunchArgs ignores dangling --workspace without value");

    var validDirs = new HashSet<string>(StringComparer.Ordinal) { "/tmp/cli", "/tmp/saved", "/Users/tester" };
    Assert(AgyBridge.ResolveInitialWorkingDirectory("/tmp/cli", "/tmp/saved", home, validDirs.Contains) == "/tmp/cli",
        "ResolveInitialWorkingDirectory prioritizes valid CLI --workspace over saved directory");
    Assert(AgyBridge.ResolveInitialWorkingDirectory("/tmp/missing", "/tmp/saved", home, validDirs.Contains) == "/tmp/saved",
        "ResolveInitialWorkingDirectory falls back to valid saved directory when CLI --workspace is invalid");
    Assert(AgyBridge.ResolveInitialWorkingDirectory("/tmp/missing", "/tmp/also-missing", home, validDirs.Contains) == "/Users/tester",
        "ResolveInitialWorkingDirectory falls back to home directory when CLI and saved directories are invalid");

    // Local slash command parsing
    Assert(SierraConsole.TryParseLocalCommand("/pwd", out var pwdKind, out var pwdArg) &&
           pwdKind == LocalCommandKind.Pwd && pwdArg == string.Empty,
        "TryParseLocalCommand matches /pwd");
    Assert(!SierraConsole.TryParseLocalCommand("/pwd extra", out _, out _),
        "TryParseLocalCommand does not intercept /pwd with extra args");
    Assert(SierraConsole.TryParseLocalCommand("/workspace ~/proj", out var wsKind, out var wsArg) &&
           wsKind == LocalCommandKind.Workspace && wsArg == "~/proj",
        "TryParseLocalCommand matches /workspace <path>");
    Assert(SierraConsole.TryParseLocalCommand("/workspace", out var wsEmptyKind, out var wsEmptyArg) &&
           wsEmptyKind == LocalCommandKind.Workspace && wsEmptyArg == string.Empty,
        "TryParseLocalCommand matches bare /workspace");
    Assert(!SierraConsole.TryParseLocalCommand("/workspace-other foo", out _, out _),
        "TryParseLocalCommand does not intercept /workspace-other");
    Assert(!SierraConsole.TryParseLocalCommand("/init", out _, out _),
        "TryParseLocalCommand forwards /init to agy");
    Assert(!SierraConsole.TryParseLocalCommand("hello", out _, out _),
        "TryParseLocalCommand forwards normal prompts to agy");

    // Workspace label formatting
    Assert(SierraConsole.FormatWorkspaceLabel(null) == "📁 (none)", "FormatWorkspaceLabel handles null");
    Assert(SierraConsole.FormatWorkspaceLabel("/") == "📁 /", "FormatWorkspaceLabel handles root '/'");
    Assert(SierraConsole.FormatWorkspaceLabel("/Users/tester/my-app/") == "📁 my-app", "FormatWorkspaceLabel shows basename");

    // AgentLeaseRegistry.ReleaseAllLeases
    var resetReg = new AgentLeaseRegistry(CharacterAgent.Roster);
    resetReg.AcquireOrResumeMainLease("conv-a", (_, _) => true);
    resetReg.AcquireSubagentLease("conv-a", "sub-1", (_, _) => true);
    Assert(resetReg.Leases.Count == 2 && resetReg.AvailableCount == 8,
        "Registry holds 2 active leases before ReleaseAllLeases");
    var releasedAll = resetReg.ReleaseAllLeases();
    Assert(releasedAll.Count == 2 && resetReg.Leases.Count == 0 && resetReg.AvailableCount == 10,
        "ReleaseAllLeases clears all leases and restores all 10 characters to available pool");
    Assert(resetReg.ReleaseAllLeases().Count == 0,
        "ReleaseAllLeases is idempotent when registry is already empty");

    Assert(SierraConsole.TryParseLocalCommand("  /PWD  ", out var pwdUpperKind, out _) &&
           pwdUpperKind == LocalCommandKind.Pwd,
        "TryParseLocalCommand matches /PWD case-insensitively with whitespace");
    Assert(SierraConsole.TryParseLocalCommand("/workspace \"/tmp/my project\"", out var wsQuotedKind, out var wsQuotedArg) &&
           wsQuotedKind == LocalCommandKind.Workspace && wsQuotedArg == "\"/tmp/my project\"",
        "TryParseLocalCommand preserves quoted argument for ResolveWorkspacePath");
}

Console.WriteLine($"Tests completed: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
