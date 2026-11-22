using System;
using System.Collections.Generic;
using Godot;

namespace AntigravityOffice;

public enum AgentMode
{
	Autonomous,
	Directed
}

public sealed class StuckTracker
{
	public const double DefaultSidestepThresholdSec = 0.35;
	public const double DefaultStuckThresholdSec = 1.8;
	public const float DefaultMinProgressDistance = 6f;

	private Vector2 _anchorPos;
	private bool _hasAnchor;

	public double TimeSinceProgressSec { get; private set; }
	public int RecoveryCount { get; private set; }

	public bool IsSidestepping => TimeSinceProgressSec >= DefaultSidestepThresholdSec;
	public bool IsStuck => TimeSinceProgressSec >= DefaultStuckThresholdSec;

	public void Reset(Vector2 currentPos, bool resetRecoveryCount = true)
	{
		_anchorPos = currentPos;
		_hasAnchor = true;
		TimeSinceProgressSec = 0.0;
		if (resetRecoveryCount)
		{
			RecoveryCount = 0;
		}
	}

	public void Update(Vector2 currentPos, double deltaSec)
	{
		if (!_hasAnchor)
		{
			Reset(currentPos);
			return;
		}

		if (currentPos.DistanceTo(_anchorPos) >= DefaultMinProgressDistance)
		{
			_anchorPos = currentPos;
			TimeSinceProgressSec = 0.0;
			return;
		}

		TimeSinceProgressSec += Math.Max(0.0, deltaSec);
	}

	public void MarkRecoveryAttempt(Vector2 currentPos)
	{
		RecoveryCount++;
		Reset(currentPos, resetRecoveryCount: false);
	}
}

public partial class CharacterAgent : CharacterBody2D
{
	public static readonly string[] Roster =
	{
		"david", "elizabeth", "james", "jennifer", "john",
		"linda", "mary", "michael", "patricia", "robert"
	};

	[Export]
	public string CharacterName { get; set; } = "david";

	[Export]
	public float MoveSpeed { get; set; } = 135f;

	public string CurrentRoom { get; private set; } = "coffee_shop";
	public AgentMode Mode { get; private set; } = AgentMode.Autonomous;

	private AnimatedSprite2D _sprite = null!;
	private NavigationAgent2D _navAgent = null!;
	private Label? _nameTag;

	private RoomManager? _roomManager;
	private readonly Random _rng = new();

	private readonly Queue<RoomHop> _pendingHops = new();
	private string? _targetRoom;
	private string? _targetPoiKind;
	private string _targetAnimation = "idle";
	private POIMarker? _claimedPoi;

	private Vector2? _moveTarget;
	private Vector2 _lastDesiredVelocity;
	private double _waitTimer;
	private double _portalRetryTimer;
	private bool _isWalking;
	private bool _preferLeftSidestep;
	private readonly StuckTracker _stuckTracker = new();
	private readonly List<Vector2> _otherAgentPositions = new();
	private readonly HashSet<POIMarker> _blockedPois = new();

	private static readonly (string AnimName, string Suffix, int FrameCount, float Fps)[] AnimationSpecs =
	{
		("idle", ".png", 2, 3.0f),
		("walk_down", "-walk-down.png", 4, 8.0f),
		("walk_up", "-walk-up.png", 4, 8.0f),
		("walk_right", "-walk-right.png", 4, 8.0f),
		("drink_coffee", "-drink-coffee.png", 2, 3.0f),
		("seated_front", "-seated-front.png", 2, 2.0f),
		("seated_back", "-seated-back.png", 2, 2.0f),
		("seated_back_typing", "-seated-back-typing.png", 2, 6.0f),
	};

	public override void _Ready()
	{
		_sprite = GetNode<AnimatedSprite2D>("AnimatedSprite2D");
		_navAgent = GetNode<NavigationAgent2D>("NavigationAgent2D");
		_nameTag = GetNodeOrNull<Label>("NameTag");

		_sprite.Centered = true;
		_sprite.Offset = new Vector2(0f, -144f);
		_sprite.SpriteFrames = BuildSpriteFrames(CharacterName);
		_sprite.Play("idle");

		if (_nameTag is not null)
		{
			_nameTag.Text = char.ToUpperInvariant(CharacterName[0]) + CharacterName[1..];
		}

		_navAgent.AvoidanceEnabled = true;
		_navAgent.MaxSpeed = MoveSpeed;
		_navAgent.Radius = 16f;
		_navAgent.TimeHorizonAgents = 0.8f;
		_navAgent.PathDesiredDistance = 12f;
		_navAgent.TargetDesiredDistance = 12f;
		_navAgent.VelocityComputed += OnVelocityComputed;

		_roomManager = GetNodeOrNull<RoomManager>("/root/RoomManager");
		if (_roomManager is not null)
		{
			_roomManager.CharacterEvicted += OnCharacterEvicted;
		}

		_waitTimer = 1.0 + _rng.NextDouble() * 3.0;
	}

	public override void _ExitTree()
	{
		if (_roomManager is not null)
		{
			_roomManager.CharacterEvicted -= OnCharacterEvicted;
		}

		_navAgent.VelocityComputed -= OnVelocityComputed;
	}

	public void InitializeInRoom(string roomId, Vector2 spawnPosition)
	{
		CurrentRoom = roomId;
		GlobalPosition = spawnPosition;
		_roomManager?.Graph.PlaceCharacterInRoom(CharacterName, roomId);
		PlayIdle();
	}

	public void AssignDirectedTask(string targetRoom, string preferredPoiKind, string targetAnimation)
	{
		Mode = AgentMode.Directed;
		_roomManager?.Graph.SetDirectedMode(CharacterName, true);

		CancelCurrentActivity();

		_targetRoom = targetRoom;
		_targetPoiKind = preferredPoiKind;
		_targetAnimation = targetAnimation;

		if (_roomManager is not null && CurrentRoom != targetRoom)
		{
			_roomManager.RequestDirectedMove(CharacterName, CurrentRoom, targetRoom);
			EnqueueRoomPath(CurrentRoom, targetRoom);
		}

		AdvanceNavigationOrPoi();
	}

	public void SetDirectedPose(string animationName)
	{
		if (Mode != AgentMode.Directed)
		{
			return;
		}

		_targetAnimation = animationName;
		if (!_isWalking && _pendingHops.Count == 0 && CurrentRoom == _targetRoom)
		{
			PlayAnimation(animationName, _claimedPoi?.FlipH ?? false);
		}
	}

	public void ReturnToAutonomous()
	{
		Mode = AgentMode.Autonomous;
		_roomManager?.Graph.SetDirectedMode(CharacterName, false);
		CancelCurrentActivity();
		PlayIdle();
		_waitTimer = 1.0 + _rng.NextDouble() * 2.5;
	}

	private void OnCharacterEvicted(string characterId, string targetRoom)
	{
		if (characterId != CharacterName || Mode == AgentMode.Directed)
		{
			return;
		}

		CancelCurrentActivity();
		_targetRoom = targetRoom;
		_targetPoiKind = null;
		_targetAnimation = "idle";
		EnqueueRoomPath(CurrentRoom, targetRoom);
		AdvanceNavigationOrPoi();
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_portalRetryTimer > 0.0)
		{
			_portalRetryTimer -= delta;
			if (_portalRetryTimer <= 0.0)
			{
				TryEnterNextPortal();
			}
			return;
		}

		if (_isWalking && _moveTarget.HasValue)
		{
			Vector2 currentPos = GlobalPosition;
			_stuckTracker.Update(currentPos, delta);

			Vector2 toTarget = _moveTarget.Value - currentPos;
			float arrivalRadius = (_pendingHops.Count > 0 && _stuckTracker.IsSidestepping) ? 36f : 14f;
			if (toTarget.Length() <= arrivalRadius)
			{
				ArriveAtDestination();
				return;
			}

			if (_stuckTracker.IsStuck)
			{
				HandleStuckWhileWalking();
				return;
			}

			if (_pendingHops.Count == 0 &&
				Mode == AgentMode.Autonomous &&
				toTarget.Length() <= 64f &&
				IsDestinationOccupiedByStationaryAgent(_moveTarget.Value, occupancyRadius: 24f))
			{
				SwitchFromOccupiedDestination();
				return;
			}

			Vector2 nextPos = _navAgent.IsNavigationFinished()
				? _moveTarget.Value
				: _navAgent.GetNextPathPosition();

			Vector2 dir = nextPos - currentPos;
			if (dir.LengthSquared() < 1f)
			{
				dir = toTarget;
			}

			IReadOnlyList<Vector2> otherAgents = CollectOtherAgentPositionsInRoom();
			Rect2? walkBounds = _roomManager is not null ? _roomManager.GetRoomWalkBounds(CurrentRoom) : null;
			Vector2 steeredDir = ComputeSteeredDirection(
				currentPos,
				dir,
				otherAgents,
				forceSidestep: _stuckTracker.IsSidestepping,
				walkBounds: walkBounds,
				preferLeftSidestep: _preferLeftSidestep);

			Vector2 desiredVelocity = steeredDir * MoveSpeed;
			_lastDesiredVelocity = desiredVelocity;
			UpdateWalkAnimation(desiredVelocity);

			if (_navAgent.AvoidanceEnabled)
			{
				_navAgent.SetVelocity(desiredVelocity);
			}
			else
			{
				Velocity = desiredVelocity;
				MoveAndSlide();
			}
			return;
		}

		if (_navAgent.AvoidanceEnabled)
		{
			_navAgent.SetVelocity(Vector2.Zero);
		}

		if (Mode == AgentMode.Autonomous)
		{
			_waitTimer -= delta;
			if (_waitTimer <= 0.0)
			{
				PickNextAutonomousAction();
			}
		}
	}

	private void OnVelocityComputed(Vector2 safeVelocity)
	{
		if (!_isWalking)
		{
			return;
		}

		if (_stuckTracker.IsSidestepping && safeVelocity.LengthSquared() < 100f && _lastDesiredVelocity.LengthSquared() > 1f)
		{
			Velocity = _lastDesiredVelocity;
		}
		else
		{
			Velocity = safeVelocity;
		}

		MoveAndSlide();
	}

	public static Vector2 ComputeSteeredDirection(
		Vector2 currentPos,
		Vector2 desiredDir,
		IReadOnlyList<Vector2>? otherAgentPositions,
		bool forceSidestep,
		float detectRadius = 48f,
		Rect2? walkBounds = null,
		bool preferLeftSidestep = false)
	{
		if (desiredDir.LengthSquared() < 0.0001f)
		{
			return Vector2.Zero;
		}

		Vector2 forward = desiredDir.Normalized();
		Vector2 right = new Vector2(-forward.Y, forward.X);

		bool blockedAhead = false;
		float blockingLateral = 0f;
		float closestDistSq = detectRadius * detectRadius;

		if (otherAgentPositions is not null)
		{
			for (int i = 0; i < otherAgentPositions.Count; i++)
			{
				Vector2 offset = otherAgentPositions[i] - currentPos;
				float distSq = offset.LengthSquared();
				if (distSq > closestDistSq || distSq < 0.01f)
				{
					continue;
				}

				float forwardDot = offset.Dot(forward);
				if (forwardDot <= 0f)
				{
					continue;
				}

				float lateralDot = offset.Dot(right);
				if (Mathf.Abs(lateralDot) < 32f)
				{
					blockedAhead = true;
					closestDistSq = distSq;
					blockingLateral = lateralDot;
				}
			}
		}

		if (!blockedAhead && !forceSidestep)
		{
			return forward;
		}

		// Right-hand rule sidestep by default (breaks head-on symmetry); step left if
		// a blocking agent is already well to our right or if recovery flipped side.
		Vector2 sidestepDir = preferLeftSidestep ? -right : right;
		if (blockedAhead && blockingLateral > 14f)
		{
			sidestepDir = -right;
		}
		else if (blockedAhead && blockingLateral < -14f)
		{
			sidestepDir = right;
		}

		if (walkBounds is { } bounds)
		{
			Vector2 probePreferred = currentPos + sidestepDir * 24f;
			Vector2 probeOpposite = currentPos - sidestepDir * 24f;
			if (!bounds.HasPoint(probePreferred) && bounds.HasPoint(probeOpposite))
			{
				sidestepDir = -sidestepDir;
			}
		}

		float forwardWeight = forceSidestep ? 0.35f : 0.65f;
		float sideWeight = forceSidestep ? 0.90f : 0.70f;
		return (forward * forwardWeight + sidestepDir * sideWeight).Normalized();
	}

	private IReadOnlyList<Vector2> CollectOtherAgentPositionsInRoom(string? roomIdOverride = null)
	{
		_otherAgentPositions.Clear();
		Node? parent = GetParent();
		if (parent is null)
		{
			return _otherAgentPositions;
		}

		string targetRoom = roomIdOverride ?? CurrentRoom;
		foreach (Node child in parent.GetChildren())
		{
			if (child is CharacterAgent other && other != this && other.CurrentRoom == targetRoom)
			{
				_otherAgentPositions.Add(other.GlobalPosition);
			}
		}

		return _otherAgentPositions;
	}

	private bool IsDestinationOccupiedByStationaryAgent(Vector2 destination, float occupancyRadius)
	{
		Node? parent = GetParent();
		if (parent is null)
		{
			return false;
		}

		float radiusSq = occupancyRadius * occupancyRadius;
		foreach (Node child in parent.GetChildren())
		{
			if (child is CharacterAgent other && other != this && other.CurrentRoom == CurrentRoom)
			{
				if ((!other._isWalking || other.Velocity.LengthSquared() < 100f) &&
					other.GlobalPosition.DistanceSquaredTo(destination) < radiusSq)
				{
					return true;
				}
			}
		}

		return false;
	}

	private void SwitchFromOccupiedDestination()
	{
		if (_roomManager is null)
		{
			return;
		}

		IReadOnlyList<Vector2> otherAgents = CollectOtherAgentPositionsInRoom();
		if (_claimedPoi is not null)
		{
			POIMarker occupiedPoi = _claimedPoi;
			occupiedPoi.Release(CharacterName);
			_claimedPoi = null;
			_blockedPois.Add(occupiedPoi);

			POIMarker? altPoi = _roomManager.ClaimAvailablePoi(
				CurrentRoom,
				CharacterName,
				_targetPoiKind,
				excludePoi: occupiedPoi,
				occupiedPositions: otherAgents,
				excludePois: _blockedPois);

			if (altPoi is not null)
			{
				_claimedPoi = altPoi;
				StartWalkingTo(altPoi.TargetGlobalPosition, resetRecoveryCount: false);
				return;
			}
		}

		Vector2 walkPoint = _roomManager.GetRandomWalkPointInRoom(CurrentRoom, otherAgents);
		StartWalkingTo(walkPoint, resetRecoveryCount: false);
	}

	private void HandleStuckWhileWalking()
	{
		_stuckTracker.MarkRecoveryAttempt(GlobalPosition);

		// 1. Stuck while trying to reach a DoorPortal
		if (_pendingHops.Count > 0)
		{
			if (_moveTarget.HasValue && GlobalPosition.DistanceTo(_moveTarget.Value) <= 56f)
			{
				ArriveAtDestination();
				return;
			}

			if (_stuckTracker.RecoveryCount >= 2)
			{
				if (Mode == AgentMode.Autonomous)
				{
					_roomManager?.Graph.CancelReservation(CharacterName);
					_pendingHops.Clear();
					_targetRoom = CurrentRoom;
					_targetPoiKind = null;
					if (_roomManager is not null)
					{
						Vector2 walkPoint = _roomManager.GetRandomWalkPointInRoom(CurrentRoom, CollectOtherAgentPositionsInRoom());
						StartWalkingTo(walkPoint, resetRecoveryCount: false);
					}
					else
					{
						CancelCurrentActivity();
						PlayIdle();
					}
					return;
				}

				TryEnterNextPortal();
				return;
			}

			_preferLeftSidestep = !_preferLeftSidestep;
			if (_moveTarget.HasValue)
			{
				StartWalkingTo(_moveTarget.Value, resetRecoveryCount: false);
			}
			return;
		}

		// 2. Stuck while trying to reach a claimed POI -> switch to another POI or walk point
		if (_claimedPoi is not null)
		{
			POIMarker blockedPoi = _claimedPoi;
			blockedPoi.Release(CharacterName);
			_claimedPoi = null;
			_blockedPois.Add(blockedPoi);

			IReadOnlyList<Vector2> otherAgents = CollectOtherAgentPositionsInRoom();
			POIMarker? altPoi = _roomManager?.ClaimAvailablePoi(
				CurrentRoom,
				CharacterName,
				_targetPoiKind,
				excludePoi: blockedPoi,
				occupiedPositions: otherAgents,
				excludePois: _blockedPois);

			if (altPoi is not null)
			{
				_claimedPoi = altPoi;
				StartWalkingTo(altPoi.TargetGlobalPosition, resetRecoveryCount: false);
				return;
			}

			if (Mode == AgentMode.Directed && _stuckTracker.RecoveryCount >= 2)
			{
				_isWalking = false;
				_moveTarget = null;
				Velocity = Vector2.Zero;
				PlayAnimation(_targetAnimation, false);
				return;
			}

			if (_roomManager is not null)
			{
				Vector2 walkPoint = _roomManager.GetRandomWalkPointInRoom(CurrentRoom, otherAgents);
				StartWalkingTo(walkPoint, resetRecoveryCount: false);
				return;
			}
		}

		// 3. Stuck while wandering to a random walk point -> pick a new clear walk point or rest
		if (_stuckTracker.RecoveryCount >= 2 || _roomManager is null)
		{
			_isWalking = false;
			_moveTarget = null;
			Velocity = Vector2.Zero;
			if (_navAgent.AvoidanceEnabled)
			{
				_navAgent.SetVelocity(Vector2.Zero);
			}

			if (Mode == AgentMode.Directed)
			{
				PlayAnimation(_targetAnimation, false);
			}
			else
			{
				PlayIdle();
				_waitTimer = 1.5 + _rng.NextDouble() * 2.5;
			}
			return;
		}

		Vector2 nextWalkPoint = _roomManager.GetRandomWalkPointInRoom(CurrentRoom, CollectOtherAgentPositionsInRoom());
		StartWalkingTo(nextWalkPoint, resetRecoveryCount: false);
	}

	private void CancelCurrentActivity()
	{
		_isWalking = false;
		_preferLeftSidestep = false;
		_moveTarget = null;
		_lastDesiredVelocity = Vector2.Zero;
		_pendingHops.Clear();
		_blockedPois.Clear();
		_portalRetryTimer = 0.0;
		_stuckTracker.Reset(GlobalPosition);
		Velocity = Vector2.Zero;
		if (_navAgent.AvoidanceEnabled)
		{
			_navAgent.SetVelocity(Vector2.Zero);
		}

		if (_claimedPoi is not null)
		{
			_claimedPoi.Release(CharacterName);
			_claimedPoi = null;
		}
		_blockedPois.Clear();
		_preferLeftSidestep = false;

		_roomManager?.ReleaseCharacterPois(CharacterName);
	}

	private void EnqueueRoomPath(string fromRoom, string toRoom)
	{
		_pendingHops.Clear();
		if (_roomManager is null || fromRoom == toRoom)
		{
			return;
		}

		foreach (RoomHop hop in _roomManager.FindRoomPath(fromRoom, toRoom))
		{
			_pendingHops.Enqueue(hop);
		}
	}

	private void AdvanceNavigationOrPoi()
	{
		if (_pendingHops.Count > 0 && _roomManager is not null)
		{
			RoomHop nextHop = _pendingHops.Peek();
			DoorPortal? portal = _roomManager.GetPortal(nextHop.FromRoom, nextHop.FromPortal);
			if (portal is not null)
			{
				StartWalkingTo(portal.GlobalPosition);
				return;
			}
			_pendingHops.Dequeue();
		}

		if (!string.IsNullOrEmpty(_targetRoom) && CurrentRoom == _targetRoom && _roomManager is not null)
		{
			IReadOnlyList<Vector2> otherAgents = CollectOtherAgentPositionsInRoom();
			if (!string.IsNullOrEmpty(_targetPoiKind) || Mode == AgentMode.Directed)
			{
				POIMarker? poi = _roomManager.ClaimAvailablePoi(
					CurrentRoom,
					CharacterName,
					_targetPoiKind,
					occupiedPositions: otherAgents);
				if (poi is not null)
				{
					_claimedPoi = poi;
					StartWalkingTo(poi.TargetGlobalPosition);
					return;
				}
			}

			Vector2 wanderPos = _roomManager.GetRandomWalkPointInRoom(CurrentRoom, otherAgents);
			StartWalkingTo(wanderPos);
		}
	}

	private void StartWalkingTo(Vector2 targetPos, bool resetRecoveryCount = true)
	{
		_moveTarget = targetPos;
		_navAgent.TargetPosition = targetPos;
		_isWalking = true;
		_stuckTracker.Reset(GlobalPosition, resetRecoveryCount);
	}

	private void ArriveAtDestination()
	{
		_isWalking = false;
		_preferLeftSidestep = false;
		_moveTarget = null;
		_lastDesiredVelocity = Vector2.Zero;
		_blockedPois.Clear();
		_stuckTracker.Reset(GlobalPosition);
		Velocity = Vector2.Zero;
		if (_navAgent.AvoidanceEnabled)
		{
			_navAgent.SetVelocity(Vector2.Zero);
		}

		if (_pendingHops.Count > 0)
		{
			TryEnterNextPortal();
			return;
		}

		if (_claimedPoi is not null)
		{
			GlobalPosition = _claimedPoi.TargetGlobalPosition;
			string anim = Mode == AgentMode.Directed ? _targetAnimation : _claimedPoi.AnimationName;
			PlayAnimation(anim, _claimedPoi.FlipH);
			_waitTimer = 5.0 + _rng.NextDouble() * 6.0;
			return;
		}

		if (Mode == AgentMode.Directed)
		{
			PlayAnimation(_targetAnimation, false);
		}
		else
		{
			PlayIdle();
			_waitTimer = 2.0 + _rng.NextDouble() * 4.0;
		}
	}

	private void TryEnterNextPortal()
	{
		if (_pendingHops.Count == 0 || _roomManager is null)
		{
			AdvanceNavigationOrPoi();
			return;
		}

		RoomHop hop = _pendingHops.Peek();
		if (_roomManager.IsPortalOnCooldown(hop.FromRoom, hop.FromPortal))
		{
			PlayIdle();
			_portalRetryTimer = 0.75;
			return;
		}

		_pendingHops.Dequeue();
		DoorPortal? destPortal = _roomManager.GetPortal(hop.ToRoom, hop.ToPortal);
		if (destPortal is not null)
		{
			Vector2 spawnPos = destPortal.ArrivalPosition + new Vector2(0f, 18f);
			IReadOnlyList<Vector2> destRoomAgents = CollectOtherAgentPositionsInRoom(hop.ToRoom);
			if (!RoomManager.IsPointClearOfAgents(spawnPos, destRoomAgents, clearance: 28f))
			{
				Vector2[] offsets =
				{
					new Vector2(28f, 18f),
					new Vector2(-28f, 18f),
					new Vector2(0f, 42f),
					new Vector2(32f, 36f),
				};
				foreach (Vector2 offset in offsets)
				{
					Vector2 candidate = destPortal.ArrivalPosition + offset;
					if (RoomManager.IsPointClearOfAgents(candidate, destRoomAgents, clearance: 28f))
					{
						spawnPos = candidate;
						break;
					}
				}
			}

			GlobalPosition = spawnPos;
		}

		_roomManager.CommitRoomTransition(CharacterName, hop.FromRoom, hop.ToRoom, hop.FromPortal, hop.ToPortal);
		CurrentRoom = hop.ToRoom;
		AdvanceNavigationOrPoi();
	}

	private void PickNextAutonomousAction()
	{
		if (_roomManager is null)
		{
			_waitTimer = 3.0;
			return;
		}

		if (_claimedPoi is not null)
		{
			_claimedPoi.Release(CharacterName);
			_claimedPoi = null;
		}
		_blockedPois.Clear();
		_preferLeftSidestep = false;

		double roll = _rng.NextDouble();
		if (roll < 0.35)
		{
			// Try moving to another room (respecting MaxCapacity = 3).
			string nextRoom = _roomManager.GetRandomOpenRoom(CurrentRoom);
			if (nextRoom != CurrentRoom && _roomManager.RequestAutonomousMove(CharacterName, CurrentRoom, nextRoom))
			{
				_targetRoom = nextRoom;
				_targetPoiKind = GetAutonomousPoiKindForRoom(nextRoom);
				_targetAnimation = "idle";
				EnqueueRoomPath(CurrentRoom, nextRoom);
				AdvanceNavigationOrPoi();
				return;
			}
		}

		IReadOnlyList<Vector2> otherAgents = CollectOtherAgentPositionsInRoom();
		if (roll < 0.70)
		{
			// Visit a local POI in the current room.
			string? poiKind = GetAutonomousPoiKindForRoom(CurrentRoom);
			POIMarker? poi = _roomManager.ClaimAvailablePoi(
				CurrentRoom,
				CharacterName,
				poiKind,
				occupiedPositions: otherAgents);
			if (poi is not null)
			{
				_claimedPoi = poi;
				_targetRoom = CurrentRoom;
				StartWalkingTo(poi.TargetGlobalPosition);
				return;
			}
		}

		// Meander within current room.
		_targetRoom = CurrentRoom;
		Vector2 walkPoint = _roomManager.GetRandomWalkPointInRoom(CurrentRoom, otherAgents);
		StartWalkingTo(walkPoint);
	}

	private static string? GetAutonomousPoiKindForRoom(string roomId) => roomId switch
	{
		"coffee_shop" => "CounterPOI",
		"conference_room" => "ConferenceSeatPOI",
		"printer_server_area" => "PrinterPOI",
		"server_room" => "ServerRackPOI",
		"corporate_office" => "DeskPOI",
		_ => null
	};

	private void UpdateWalkAnimation(Vector2 velocity)
	{
		if (Mathf.Abs(velocity.X) > Mathf.Abs(velocity.Y))
		{
			_sprite.FlipH = velocity.X < 0f;
			if (_sprite.Animation != "walk_right")
			{
				_sprite.Play("walk_right");
			}
		}
		else
		{
			_sprite.FlipH = false;
			string anim = velocity.Y >= 0f ? "walk_down" : "walk_up";
			if (_sprite.Animation != anim)
			{
				_sprite.Play(anim);
			}
		}
	}

	private void PlayIdle()
	{
		PlayAnimation("idle", false);
	}

	private void PlayAnimation(string animName, bool flipH)
	{
		_sprite.FlipH = flipH;
		if (_sprite.SpriteFrames is not null && _sprite.SpriteFrames.HasAnimation(animName))
		{
			_sprite.Play(animName);
		}
		else
		{
			_sprite.Play("idle");
		}
	}

	private static SpriteFrames BuildSpriteFrames(string characterName)
	{
		var frames = new SpriteFrames();
		if (frames.HasAnimation("default"))
		{
			frames.RemoveAnimation("default");
		}

		foreach (var (animName, suffix, frameCount, fps) in AnimationSpecs)
		{
			string path = $"res://assets/sprites/{characterName}{suffix}";
			Texture2D? sheet = ResourceLoader.Load<Texture2D>(path);
			frames.AddAnimation(animName);
			frames.SetAnimationLoopMode(animName, SpriteFrames.LoopMode.Linear);
			frames.SetAnimationSpeed(animName, fps);

			if (sheet is null)
			{
				continue;
			}

			int cellWidth = Math.Max(1, sheet.GetWidth() / frameCount);
			int cellHeight = sheet.GetHeight();

			for (int i = 0; i < frameCount; i++)
			{
				var atlas = new AtlasTexture
				{
					Atlas = sheet,
					Region = new Rect2(i * cellWidth, 0, cellWidth, cellHeight)
				};
				frames.AddFrame(animName, atlas);
			}
		}

		return frames;
	}
}
