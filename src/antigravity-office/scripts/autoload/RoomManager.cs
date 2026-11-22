using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace AntigravityOffice;

public sealed record RoomHop(string FromRoom, string FromPortal, string ToRoom, string ToPortal);

public sealed record EvictionNotice(string EvictedCharacter, string TargetRoom);

public sealed class RoomGraphState
{
    public const int MaxCapacity = 3;

    public static readonly string[] AllRooms =
    {
        "conference_room",
        "corporate_office",
        "coffee_shop",
        "printer_server_area",
        "server_room"
    };

    private static readonly string[] EvictionPreferredRooms =
    {
        "coffee_shop",
        "printer_server_area",
        "server_room",
        "conference_room",
        "corporate_office"
    };

    private readonly Dictionary<string, List<RoomHop>> _adjacency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _occupants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _incomingReservations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directedCharacters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _portalCooldownUntil = new(StringComparer.Ordinal);

    public RoomGraphState()
    {
        foreach (string room in AllRooms)
        {
            _adjacency[room] = new List<RoomHop>();
            _occupants[room] = new HashSet<string>(StringComparer.Ordinal);
            _incomingReservations[room] = new HashSet<string>(StringComparer.Ordinal);
        }

        AddBidirectionalEdge("conference_room", "DoorRight", "corporate_office", "DoorLeft");
        AddBidirectionalEdge("corporate_office", "DoorRight", "coffee_shop", "DoorLeft");
        AddBidirectionalEdge("coffee_shop", "DoorRight", "printer_server_area", "DoorLeft");
        AddBidirectionalEdge("printer_server_area", "DoorTop", "server_room", "DoorLeft");
    }

    private void AddBidirectionalEdge(string roomA, string portalA, string roomB, string portalB)
    {
        _adjacency[roomA].Add(new RoomHop(roomA, portalA, roomB, portalB));
        _adjacency[roomB].Add(new RoomHop(roomB, portalB, roomA, portalA));
    }

    public IReadOnlyList<RoomHop> FindRoomPath(string fromRoom, string toRoom)
    {
        if (string.Equals(fromRoom, toRoom, StringComparison.Ordinal))
        {
            return Array.Empty<RoomHop>();
        }

        if (!_adjacency.ContainsKey(fromRoom) || !_adjacency.ContainsKey(toRoom))
        {
            return Array.Empty<RoomHop>();
        }

        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { fromRoom };
        var parent = new Dictionary<string, RoomHop>(StringComparer.Ordinal);

        queue.Enqueue(fromRoom);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (current == toRoom)
            {
                break;
            }

            foreach (RoomHop hop in _adjacency[current])
            {
                if (visited.Add(hop.ToRoom))
                {
                    parent[hop.ToRoom] = hop;
                    queue.Enqueue(hop.ToRoom);
                }
            }
        }

        if (!parent.ContainsKey(toRoom))
        {
            return Array.Empty<RoomHop>();
        }

        var path = new List<RoomHop>();
        string cursor = toRoom;
        while (cursor != fromRoom)
        {
            RoomHop hop = parent[cursor];
            path.Add(hop);
            cursor = hop.FromRoom;
        }

        path.Reverse();
        return path;
    }

    public int GetEffectiveOccupancy(string roomId)
    {
        if (!_occupants.TryGetValue(roomId, out var occ) || !_incomingReservations.TryGetValue(roomId, out var res))
        {
            return 0;
        }

        return occ.Count + res.Count;
    }

    public IReadOnlyCollection<string> GetOccupants(string roomId)
    {
        return _occupants.TryGetValue(roomId, out var occ) ? occ : Array.Empty<string>();
    }

    public IReadOnlyCollection<string> GetReservations(string roomId)
    {
        return _incomingReservations.TryGetValue(roomId, out var res) ? res : Array.Empty<string>();
    }

    public string? GetCharacterRoom(string characterId)
    {
        foreach (var (roomId, occ) in _occupants)
        {
            if (occ.Contains(characterId))
            {
                return roomId;
            }
        }

        return null;
    }

    public void SetDirectedMode(string characterId, bool isDirected)
    {
        if (isDirected)
        {
            _directedCharacters.Add(characterId);
        }
        else
        {
            _directedCharacters.Remove(characterId);
        }
    }

    public bool IsDirected(string characterId) => _directedCharacters.Contains(characterId);

    public void PlaceCharacterInRoom(string characterId, string roomId)
    {
        if (!_occupants.ContainsKey(roomId))
        {
            return;
        }

        foreach (var occ in _occupants.Values)
        {
            occ.Remove(characterId);
        }

        foreach (var res in _incomingReservations.Values)
        {
            res.Remove(characterId);
        }

        _occupants[roomId].Add(characterId);
    }

    public void CancelReservation(string characterId)
    {
        foreach (var res in _incomingReservations.Values)
        {
            res.Remove(characterId);
        }
    }

    public bool RequestAutonomousMove(string characterId, string fromRoom, string toRoom)
    {
        if (!_occupants.ContainsKey(toRoom))
        {
            return false;
        }

        if (fromRoom == toRoom || _occupants[toRoom].Contains(characterId))
        {
            CancelReservation(characterId);
            return true;
        }

        if (_incomingReservations[toRoom].Contains(characterId))
        {
            return true;
        }

        if (GetEffectiveOccupancy(toRoom) >= MaxCapacity)
        {
            return false;
        }

        CancelReservation(characterId);
        _incomingReservations[toRoom].Add(characterId);
        return true;
    }

    public bool RequestDirectedMove(string characterId, string fromRoom, string toRoom, out EvictionNotice? eviction)
    {
        eviction = null;

        if (!_occupants.ContainsKey(toRoom))
        {
            return false;
        }

        _directedCharacters.Add(characterId);

        if (fromRoom == toRoom || _occupants[toRoom].Contains(characterId))
        {
            CancelReservation(characterId);
            return true;
        }

        if (_incomingReservations[toRoom].Contains(characterId))
        {
            return true;
        }

        if (GetEffectiveOccupancy(toRoom) < MaxCapacity)
        {
            CancelReservation(characterId);
            _incomingReservations[toRoom].Add(characterId);
            return true;
        }

        // Priority eviction: find an autonomous reservation first, then an autonomous occupant.
        string? candidate = _incomingReservations[toRoom].FirstOrDefault(c => !_directedCharacters.Contains(c));
        bool wasReservation = candidate is not null;

        if (candidate is null)
        {
            candidate = _occupants[toRoom].FirstOrDefault(c => !_directedCharacters.Contains(c));
        }

        if (candidate is null)
        {
            // All 3 slots are held by Directed agents.
            return false;
        }

        string? openRoom = FindOpenRoom(excludeRoom: toRoom);
        if (openRoom is null)
        {
            return false;
        }

        if (wasReservation)
        {
            _incomingReservations[toRoom].Remove(candidate);
        }
        else
        {
            _occupants[toRoom].Remove(candidate);
        }

        if (!_occupants[openRoom].Contains(candidate))
        {
            _incomingReservations[openRoom].Add(candidate);
        }
        eviction = new EvictionNotice(candidate, openRoom);

        CancelReservation(characterId);
        _incomingReservations[toRoom].Add(characterId);
        return true;
    }

    public string? FindOpenRoom(string? excludeRoom = null)
    {
        foreach (string room in EvictionPreferredRooms)
        {
            if (room == excludeRoom)
            {
                continue;
            }

            if (GetEffectiveOccupancy(room) < MaxCapacity)
            {
                return room;
            }
        }

        return null;
    }

    public void CommitRoomTransition(string characterId, string fromRoom, string toRoom)
    {
        if (_occupants.TryGetValue(fromRoom, out var fromOcc))
        {
            fromOcc.Remove(characterId);
        }

        if (_incomingReservations.TryGetValue(toRoom, out var toRes))
        {
            toRes.Remove(characterId);
        }

        if (_occupants.TryGetValue(toRoom, out var toOcc))
        {
            toOcc.Add(characterId);
        }
    }

    public bool IsPortalOnCooldown(string roomId, string portalName, double currentTimeSec)
    {
        string key = $"{roomId}:{portalName}";
        return _portalCooldownUntil.TryGetValue(key, out double until) && currentTimeSec < until;
    }

    public void TriggerPortalCooldown(string roomId, string portalName, double currentTimeSec, double cooldownSec)
    {
        double clamped = Math.Clamp(cooldownSec, 2.0, 4.0);
        string key = $"{roomId}:{portalName}";
        _portalCooldownUntil[key] = currentTimeSec + clamped;
    }

    public Dictionary<string, string> SeedCharacters(IEnumerable<string> characterNames, Random? rng = null)
    {
        rng ??= new Random();

        foreach (var occ in _occupants.Values)
        {
            occ.Clear();
        }
        foreach (var res in _incomingReservations.Values)
        {
            res.Clear();
        }
        _directedCharacters.Clear();

        var shuffledNames = characterNames.OrderBy(_ => rng.Next()).ToList();
        var assignments = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string name in shuffledNames)
        {
            var availableRooms = AllRooms.Where(r => GetEffectiveOccupancy(r) < MaxCapacity).ToList();
            if (availableRooms.Count == 0)
            {
                break;
            }

            string chosen = availableRooms[rng.Next(availableRooms.Count)];
            _occupants[chosen].Add(name);
            assignments[name] = chosen;
        }

        return assignments;
    }
}

public partial class RoomManager : Node
{
    public const int MaxCapacity = RoomGraphState.MaxCapacity;

    [Signal]
    public delegate void CharacterEvictedEventHandler(string characterId, string targetRoom);

    public RoomGraphState Graph { get; } = new();

    private readonly Dictionary<string, Dictionary<string, DoorPortal>> _portals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<POIMarker>> _pois = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Rect2> _roomBounds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Rect2> _roomWalkBounds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Rect2>> _roomObstacles = new(StringComparer.Ordinal);
    private readonly Random _rng = new();

    public override void _Ready()
    {
        foreach (string room in RoomGraphState.AllRooms)
        {
            _portals[room] = new Dictionary<string, DoorPortal>(StringComparer.Ordinal);
            _pois[room] = new List<POIMarker>();
            _roomObstacles[room] = new List<Rect2>();
        }
    }

    public void RegisterRoomBounds(string roomId, Rect2 bounds)
    {
        _roomBounds[roomId] = bounds;
    }

    public void RegisterRoomWalkBounds(string roomId, Rect2 walkBounds)
    {
        _roomWalkBounds[roomId] = walkBounds;
    }

    public void RegisterRoomObstacle(string roomId, Rect2 worldRect)
    {
        if (string.IsNullOrEmpty(roomId) || worldRect.Size.X <= 0f || worldRect.Size.Y <= 0f)
        {
            return;
        }

        if (!_roomObstacles.TryGetValue(roomId, out var list))
        {
            list = new List<Rect2>();
            _roomObstacles[roomId] = list;
        }

        list.Add(worldRect);
    }

    public IReadOnlyList<Rect2> GetRoomObstacles(string roomId)
    {
        return _roomObstacles.TryGetValue(roomId, out var list)
            ? list
            : Array.Empty<Rect2>();
    }

    public static bool IsPointBlockedByObstacles(Vector2 point, IReadOnlyList<Rect2> obstacles, float padding = 20f)
    {
        for (int i = 0; i < obstacles.Count; i++)
        {
            if (obstacles[i].Grow(padding).HasPoint(point))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsPointBlockedForAgent(
        Vector2 point,
        IReadOnlyList<Rect2> obstacles,
        float horizontalPadding = 18f,
        float topPadding = 6f,
        float bottomPadding = 30f)
    {
        for (int i = 0; i < obstacles.Count; i++)
        {
            Rect2 r = obstacles[i];
            if (point.X >= r.Position.X - horizontalPadding &&
                point.X <= r.End.X + horizontalPadding &&
                point.Y >= r.Position.Y - topPadding &&
                point.Y <= r.End.Y + bottomPadding)
            {
                return true;
            }
        }

        return false;
    }

    public static Rect2 ComputeDefaultWalkBounds(string roomId, Rect2 roomBounds)
    {
        float minX = roomBounds.Position.X + 180f;
        float maxX = roomBounds.End.X - 180f;
        float minYOffset = roomId switch
        {
            "server_room" => 580f,
            "printer_server_area" => 575f,
            _ => 460f,
        };
        float minY = roomBounds.Position.Y + minYOffset;
        float maxY = roomBounds.End.Y - 70f;
        return new Rect2(minX, minY, Math.Max(10f, maxX - minX), Math.Max(10f, maxY - minY));
    }

    public static Rect2 ComputeWalkBoundsFromPolygon(
        Vector2[] vertices,
        Vector2 globalOffset,
        float horizontalPadding = 50f,
        float bottomPadding = 12f)
    {
        if (vertices.Length == 0)
        {
            return new Rect2();
        }

        float minX = float.MaxValue;
        float maxX = float.MinValue;
        float minY = float.MaxValue;
        float maxY = float.MinValue;

        foreach (Vector2 v in vertices)
        {
            Vector2 p = v + globalOffset;
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        float paddedMinX = minX + horizontalPadding;
        float paddedMaxX = maxX - horizontalPadding;
        float paddedMaxY = maxY - bottomPadding;

        if (paddedMaxX < paddedMinX + 10f)
        {
            paddedMinX = minX;
            paddedMaxX = maxX;
        }

        if (paddedMaxY < minY + 10f)
        {
            paddedMaxY = maxY;
        }

        return new Rect2(paddedMinX, minY, Math.Max(10f, paddedMaxX - paddedMinX), Math.Max(10f, paddedMaxY - minY));
    }

    public Rect2 GetRoomBounds(string roomId)
    {
        return _roomBounds.TryGetValue(roomId, out Rect2 bounds)
            ? bounds
            : new Rect2(0, 0, 1376, 768);
    }

    public void RegisterPortal(DoorPortal portal)
    {
        if (string.IsNullOrEmpty(portal.RoomId) || string.IsNullOrEmpty(portal.PortalName))
        {
            return;
        }

        if (!_portals.TryGetValue(portal.RoomId, out var roomPortals))
        {
            roomPortals = new Dictionary<string, DoorPortal>(StringComparer.Ordinal);
            _portals[portal.RoomId] = roomPortals;
        }

        roomPortals[portal.PortalName] = portal;
    }

    public DoorPortal? GetPortal(string roomId, string portalName)
    {
        if (_portals.TryGetValue(roomId, out var roomPortals) &&
            roomPortals.TryGetValue(portalName, out var portal))
        {
            return portal;
        }

        return null;
    }

    public void RegisterPoi(POIMarker poi)
    {
        if (string.IsNullOrEmpty(poi.RoomId))
        {
            return;
        }

        if (!_pois.TryGetValue(poi.RoomId, out var list))
        {
            list = new List<POIMarker>();
            _pois[poi.RoomId] = list;
        }

        if (!list.Contains(poi))
        {
            list.Add(poi);
        }
    }

    public POIMarker? ClaimAvailablePoi(
        string roomId,
        string characterId,
        string? preferredKind = null,
        POIMarker? excludePoi = null,
        IReadOnlyList<Vector2>? occupiedPositions = null,
        IReadOnlyCollection<POIMarker>? excludePois = null)
    {
        if (!_pois.TryGetValue(roomId, out var list))
        {
            return null;
        }

        IEnumerable<POIMarker> candidates = list.Where(p =>
            p != excludePoi &&
            (excludePois is null || !excludePois.Contains(p)) &&
            (p.IsAvailable || p.OccupiedBy == characterId));

        if (!string.IsNullOrEmpty(preferredKind))
        {
            var matching = candidates.Where(p => p.PoiKind == preferredKind).ToList();
            if (matching.Count > 0)
            {
                POIMarker chosen = PickLeastCrowdedPoi(matching, occupiedPositions, _rng);
                chosen.TryClaim(characterId);
                return chosen;
            }
        }

        var fallback = candidates.ToList();
        if (fallback.Count == 0)
        {
            return null;
        }

        POIMarker selected = PickLeastCrowdedPoi(fallback, occupiedPositions, _rng);
        selected.TryClaim(characterId);
        return selected;
    }

    public static POIMarker PickLeastCrowdedPoi(
        IReadOnlyList<POIMarker> candidates,
        IReadOnlyList<Vector2>? occupiedPositions,
        Random rng)
    {
        if (candidates.Count == 1 || occupiedPositions is null || occupiedPositions.Count == 0)
        {
            return candidates[rng.Next(candidates.Count)];
        }

        var positions = new Vector2[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            positions[i] = candidates[i].TargetGlobalPosition;
        }

        int idx = SelectBestPoiIndex(positions, occupiedPositions, rng);
        return candidates[idx];
    }

    public static int SelectBestPoiIndex(
        IReadOnlyList<Vector2> poiPositions,
        IReadOnlyList<Vector2>? occupiedPositions,
        Random rng,
        float clearance = 32f)
    {
        if (poiPositions.Count <= 1 || occupiedPositions is null || occupiedPositions.Count == 0)
        {
            return poiPositions.Count == 0 ? -1 : rng.Next(poiPositions.Count);
        }

        var uncrowdedIndices = new List<int>();
        for (int i = 0; i < poiPositions.Count; i++)
        {
            if (IsPointClearOfAgents(poiPositions[i], occupiedPositions, clearance))
            {
                uncrowdedIndices.Add(i);
            }
        }

        if (uncrowdedIndices.Count > 0)
        {
            return uncrowdedIndices[rng.Next(uncrowdedIndices.Count)];
        }

        // All POIs are within clearance; pick the one farthest from the nearest occupied agent.
        int bestIdx = 0;
        float bestMinDistSq = -1f;
        for (int i = 0; i < poiPositions.Count; i++)
        {
            float minDistSq = float.MaxValue;
            for (int j = 0; j < occupiedPositions.Count; j++)
            {
                float dSq = poiPositions[i].DistanceSquaredTo(occupiedPositions[j]);
                if (dSq < minDistSq)
                {
                    minDistSq = dSq;
                }
            }

            if (minDistSq > bestMinDistSq)
            {
                bestMinDistSq = minDistSq;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    public void ReleaseCharacterPois(string characterId)
    {
        foreach (var list in _pois.Values)
        {
            foreach (POIMarker poi in list)
            {
                poi.Release(characterId);
            }
        }
    }

    public IReadOnlyList<RoomHop> FindRoomPath(string fromRoom, string toRoom)
    {
        return Graph.FindRoomPath(fromRoom, toRoom);
    }

    public bool RequestAutonomousMove(string characterId, string fromRoom, string toRoom)
    {
        return Graph.RequestAutonomousMove(characterId, fromRoom, toRoom);
    }

    public bool RequestDirectedMove(string characterId, string fromRoom, string toRoom)
    {
        bool ok = Graph.RequestDirectedMove(characterId, fromRoom, toRoom, out EvictionNotice? eviction);
        if (ok && eviction is not null)
        {
            EmitSignal(SignalName.CharacterEvicted, eviction.EvictedCharacter, eviction.TargetRoom);
        }

        return ok;
    }

    public void CommitRoomTransition(string characterId, string fromRoom, string toRoom, string fromPortalName, string toPortalName)
    {
        Graph.CommitRoomTransition(characterId, fromRoom, toRoom);
        double now = Time.GetTicksMsec() / 1000.0;
        double cooldown = 2.0 + _rng.NextDouble() * 2.0; // 2.0 - 4.0s
        Graph.TriggerPortalCooldown(fromRoom, fromPortalName, now, cooldown);
        Graph.TriggerPortalCooldown(toRoom, toPortalName, now, cooldown);
    }

    public bool IsPortalOnCooldown(string roomId, string portalName)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        return Graph.IsPortalOnCooldown(roomId, portalName, now);
    }

    public string GetRandomOpenRoom(string? excludeRoom = null)
    {
        var candidates = RoomGraphState.AllRooms
            .Where(r => r != excludeRoom && Graph.GetEffectiveOccupancy(r) < MaxCapacity)
            .ToList();

        if (candidates.Count == 0)
        {
            return excludeRoom ?? "coffee_shop";
        }

        return candidates[_rng.Next(candidates.Count)];
    }

    public Rect2 GetRoomWalkBounds(string roomId)
    {
        return _roomWalkBounds.TryGetValue(roomId, out Rect2 registered)
            ? registered
            : ComputeDefaultWalkBounds(roomId, GetRoomBounds(roomId));
    }

    public Vector2 GetRandomWalkPointInRoom(string roomId, IReadOnlyList<Vector2>? occupiedPositions = null)
    {
        Rect2 walkBounds = GetRoomWalkBounds(roomId);

        return PickWalkPointInBounds(walkBounds, GetRoomObstacles(roomId), _rng, occupiedPositions: occupiedPositions);
    }

    public static bool IsPointClearOfAgents(
        Vector2 point,
        IReadOnlyList<Vector2>? occupiedPositions,
        float clearance = 44f)
    {
        if (occupiedPositions is null || occupiedPositions.Count == 0)
        {
            return true;
        }

        float clearanceSq = clearance * clearance;
        for (int i = 0; i < occupiedPositions.Count; i++)
        {
            if (point.DistanceSquaredTo(occupiedPositions[i]) < clearanceSq)
            {
                return false;
            }
        }

        return true;
    }

    public static Vector2 PickWalkPointInBounds(
        Rect2 walkBounds,
        IReadOnlyList<Rect2> obstacles,
        Random rng,
        float padding = 20f,
        IReadOnlyList<Vector2>? occupiedPositions = null,
        float agentClearance = 44f)
    {
        const int maxAttempts = 32;
        Vector2? bestObstacleFree = null;
        float bestMinDistSq = -1f;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            float x = (float)(walkBounds.Position.X + rng.NextDouble() * Math.Max(10f, walkBounds.Size.X));
            float y = (float)(walkBounds.Position.Y + rng.NextDouble() * Math.Max(10f, walkBounds.Size.Y));
            Vector2 candidate = new Vector2(x, y);
            if (IsPointBlockedForAgent(candidate, obstacles) ||
                IsPointBlockedByObstacles(candidate, obstacles, padding))
            {
                continue;
            }

            if (IsPointClearOfAgents(candidate, occupiedPositions, agentClearance))
            {
                return candidate;
            }

            if (occupiedPositions is not null && occupiedPositions.Count > 0)
            {
                float minDistSq = float.MaxValue;
                for (int i = 0; i < occupiedPositions.Count; i++)
                {
                    float dSq = candidate.DistanceSquaredTo(occupiedPositions[i]);
                    if (dSq < minDistSq)
                    {
                        minDistSq = dSq;
                    }
                }

                if (minDistSq > bestMinDistSq)
                {
                    bestMinDistSq = minDistSq;
                    bestObstacleFree = candidate;
                }
            }
        }

        if (bestObstacleFree.HasValue)
        {
            return bestObstacleFree.Value;
        }

        return new Vector2(
            walkBounds.Position.X + Math.Min(16f, walkBounds.Size.X * 0.5f),
            walkBounds.End.Y - Math.Min(16f, walkBounds.Size.Y * 0.5f));
    }
}
