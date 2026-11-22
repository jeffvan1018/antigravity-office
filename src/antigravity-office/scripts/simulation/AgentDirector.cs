using System;
using System.Collections.Generic;
using Godot;

namespace AntigravityOffice;

public enum LeaseState
{
    ActiveTyping,
    WaitingInput
}

public sealed class AgentLease
{
    public required string LeaseKey { get; init; }
    public required string CharacterName { get; init; }
    public required string TargetRoom { get; set; }
    public required string TargetPoiKind { get; set; }
    public LeaseState State { get; set; } = LeaseState.ActiveTyping;
    public double GraceTimeRemainingSec { get; set; }
}

public sealed class AgentLeaseRegistry
{
    public const double GraceDurationSec = 12.0;

    private readonly Dictionary<string, AgentLease> _leases = new(StringComparer.Ordinal);
    private readonly Queue<string> _availableCharacters = new();
    private readonly HashSet<string> _leasedCharacters = new(StringComparer.Ordinal);

    public AgentLeaseRegistry(IEnumerable<string> roster)
    {
        foreach (string name in roster)
        {
            _availableCharacters.Enqueue(name);
        }
    }

    public IReadOnlyDictionary<string, AgentLease> Leases => _leases;
    public int AvailableCount => _availableCharacters.Count;

    public AgentLease? AcquireOrResumeMainLease(
        string conversationId,
        Func<string, string, bool> tryReserveRoom)
    {
        string key = NormalizeKey(conversationId);
        if (_leases.TryGetValue(key, out AgentLease? existing))
        {
            existing.State = LeaseState.ActiveTyping;
            existing.GraceTimeRemainingSec = 0.0;
            return existing;
        }

        if (_availableCharacters.Count == 0)
        {
            return null;
        }

        string character = DequeueAvailableCharacter();
        if (string.IsNullOrEmpty(character))
        {
            return null;
        }

        string targetRoom = "corporate_office";
        string poiKind = "DeskPOI";
        if (!tryReserveRoom(character, "corporate_office"))
        {
            targetRoom = "conference_room";
            poiKind = "ConferenceSeatPOI";
            if (!tryReserveRoom(character, targetRoom))
            {
                _availableCharacters.Enqueue(character);
                return null;
            }
        }

        var lease = new AgentLease
        {
            LeaseKey = key,
            CharacterName = character,
            TargetRoom = targetRoom,
            TargetPoiKind = poiKind,
            State = LeaseState.ActiveTyping
        };

        _leases[key] = lease;
        _leasedCharacters.Add(character);
        return lease;
    }

    public AgentLease? AcquireSubagentLease(
        string conversationId,
        string subagentId,
        Func<string, string, bool> tryReserveRoom)
    {
        string key = $"sub:{NormalizeKey(conversationId)}:{subagentId}";
        if (_leases.TryGetValue(key, out AgentLease? existing))
        {
            return existing;
        }

        if (_availableCharacters.Count == 0)
        {
            return null;
        }

        string character = DequeueAvailableCharacter();
        if (string.IsNullOrEmpty(character))
        {
            return null;
        }

        string targetRoom = "corporate_office";
        string poiKind = "DeskPOI";
        if (!tryReserveRoom(character, "corporate_office"))
        {
            targetRoom = "conference_room";
            poiKind = "ConferenceSeatPOI";
            if (!tryReserveRoom(character, targetRoom))
            {
                _availableCharacters.Enqueue(character);
                return null;
            }
        }

        var lease = new AgentLease
        {
            LeaseKey = key,
            CharacterName = character,
            TargetRoom = targetRoom,
            TargetPoiKind = poiKind,
            State = LeaseState.ActiveTyping
        };

        _leases[key] = lease;
        _leasedCharacters.Add(character);
        return lease;
    }

    public AgentLease? BeginGracePeriod(string conversationId)
    {
        string key = NormalizeKey(conversationId);
        if (!_leases.TryGetValue(key, out AgentLease? lease))
        {
            return null;
        }

        lease.State = LeaseState.WaitingInput;
        lease.GraceTimeRemainingSec = GraceDurationSec;
        return lease;
    }

    public AgentLease? ReleaseSubagentLease(string conversationId, string subagentId)
    {
        string key = $"sub:{NormalizeKey(conversationId)}:{subagentId}";
        return ReleaseLeaseByKey(key);
    }

    public IReadOnlyList<AgentLease> TickGraceTimers(double deltaSec)
    {
        var expired = new List<AgentLease>();
        foreach (AgentLease lease in _leases.Values)
        {
            if (lease.State != LeaseState.WaitingInput)
            {
                continue;
            }

            lease.GraceTimeRemainingSec -= deltaSec;
            if (lease.GraceTimeRemainingSec <= 0.0)
            {
                expired.Add(lease);
            }
        }

        foreach (AgentLease lease in expired)
        {
            ReleaseLeaseByKey(lease.LeaseKey);
        }

        return expired;
    }

    public AgentLease? ReleaseLeaseByKey(string key)
    {
        if (!_leases.Remove(key, out AgentLease? lease))
        {
            return null;
        }

        if (_leasedCharacters.Remove(lease.CharacterName))
        {
            _availableCharacters.Enqueue(lease.CharacterName);
        }

        return lease;
    }

    public IReadOnlyList<AgentLease> ReleaseAllLeases()
    {
        if (_leases.Count == 0)
        {
            return Array.Empty<AgentLease>();
        }

        var released = new List<AgentLease>(_leases.Values);
        _leases.Clear();
        _leasedCharacters.Clear();
        foreach (AgentLease lease in released)
        {
            _availableCharacters.Enqueue(lease.CharacterName);
        }

        return released;
    }

    private string DequeueAvailableCharacter()
    {
        while (_availableCharacters.Count > 0)
        {
            string candidate = _availableCharacters.Dequeue();
            if (!_leasedCharacters.Contains(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string NormalizeKey(string conversationId)
    {
        return string.IsNullOrWhiteSpace(conversationId) ? "default" : conversationId;
    }
}

public partial class AgentDirector : Node
{
    private AgyBridge? _agyBridge;
    private RoomManager? _roomManager;
    private readonly Dictionary<string, CharacterAgent> _agents = new(StringComparer.Ordinal);
    private readonly AgentLeaseRegistry _registry = new(CharacterAgent.Roster);

    public override void _Ready()
    {
        _agyBridge = GetNodeOrNull<AgyBridge>("/root/AgyBridge");
        _roomManager = GetNodeOrNull<RoomManager>("/root/RoomManager");

        if (_agyBridge is not null)
        {
            _agyBridge.SessionInitialized += OnSessionInitialized;
            _agyBridge.AgentTextDelta += OnAgentTextDelta;
            _agyBridge.ToolStepUpdated += OnToolStepUpdated;
            _agyBridge.SubagentUpdated += OnSubagentUpdated;
            _agyBridge.TurnCompleted += OnTurnCompleted;
            _agyBridge.SessionReset += OnSessionReset;
        }
    }

    public override void _ExitTree()
    {
        if (_agyBridge is not null)
        {
            _agyBridge.SessionInitialized -= OnSessionInitialized;
            _agyBridge.AgentTextDelta -= OnAgentTextDelta;
            _agyBridge.ToolStepUpdated -= OnToolStepUpdated;
            _agyBridge.SubagentUpdated -= OnSubagentUpdated;
            _agyBridge.TurnCompleted -= OnTurnCompleted;
            _agyBridge.SessionReset -= OnSessionReset;
        }
    }

    public void RegisterAgent(CharacterAgent agent)
    {
        _agents[agent.CharacterName] = agent;
    }

    public override void _Process(double delta)
    {
        IReadOnlyList<AgentLease> expired = _registry.TickGraceTimers(delta);
        foreach (AgentLease lease in expired)
        {
            if (_agents.TryGetValue(lease.CharacterName, out CharacterAgent? agent))
            {
                agent.ReturnToAutonomous();
            }
        }
    }

    private void OnSessionInitialized(string conversationId)
    {
        EnsureMainAgentActive(conversationId);
    }

    private void OnAgentTextDelta(string conversationId, string _textDelta)
    {
        EnsureMainAgentActive(conversationId);
    }

    private void OnToolStepUpdated(string conversationId, string _toolName, string _state)
    {
        EnsureMainAgentActive(conversationId);
    }

    private void OnSubagentUpdated(string conversationId, string subagentId, string _name, string state)
    {
        if (string.IsNullOrWhiteSpace(subagentId))
        {
            subagentId = "subagent";
        }

        if (state.Equals("DONE", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("FINISHED", StringComparison.OrdinalIgnoreCase))
        {
            AgentLease? released = _registry.ReleaseSubagentLease(conversationId, subagentId);
            if (released is not null && _agents.TryGetValue(released.CharacterName, out CharacterAgent? agent))
            {
                agent.ReturnToAutonomous();
            }
            return;
        }

        AgentLease? lease = _registry.AcquireSubagentLease(conversationId, subagentId, TryReserveRoomForCharacter);
        if (lease is null || !_agents.TryGetValue(lease.CharacterName, out CharacterAgent? subAgent))
        {
            return;
        }

        string anim = lease.TargetRoom == "corporate_office" ? "seated_back_typing" : "seated_front";
        if (subAgent.Mode != AgentMode.Directed)
        {
            subAgent.AssignDirectedTask(lease.TargetRoom, lease.TargetPoiKind, anim);
        }
    }

    private void OnTurnCompleted(string conversationId, string _status, string _error, string _response)
    {
        AgentLease? lease = _registry.BeginGracePeriod(conversationId);
        if (lease is not null && _agents.TryGetValue(lease.CharacterName, out CharacterAgent? agent))
        {
            agent.SetDirectedPose("seated_back");
        }
    }

    private void OnSessionReset()
    {
        IReadOnlyList<AgentLease> released = _registry.ReleaseAllLeases();
        foreach (AgentLease lease in released)
        {
            if (_agents.TryGetValue(lease.CharacterName, out CharacterAgent? agent))
            {
                agent.ReturnToAutonomous();
            }
        }
    }

    private void EnsureMainAgentActive(string conversationId)
    {
        bool wasAlreadyLeased = _registry.Leases.ContainsKey(string.IsNullOrWhiteSpace(conversationId) ? "default" : conversationId);
        AgentLease? lease = _registry.AcquireOrResumeMainLease(conversationId, TryReserveRoomForCharacter);
        if (lease is null || !_agents.TryGetValue(lease.CharacterName, out CharacterAgent? agent))
        {
            return;
        }

        if (wasAlreadyLeased)
        {
            agent.SetDirectedPose("seated_back_typing");
        }
        else
        {
            agent.AssignDirectedTask(lease.TargetRoom, lease.TargetPoiKind, "seated_back_typing");
        }
    }

    private bool TryReserveRoomForCharacter(string characterName, string targetRoom)
    {
        if (_roomManager is null || !_agents.TryGetValue(characterName, out CharacterAgent? agent))
        {
            return true;
        }

        return _roomManager.RequestDirectedMove(characterName, agent.CurrentRoom, targetRoom);
    }
}
