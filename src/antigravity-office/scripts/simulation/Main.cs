using System;
using System.Collections.Generic;
using Godot;

namespace AntigravityOffice;

public partial class Main : Node
{
	private static readonly (string RoomId, string DisplayName, float XOffset)[] RoomLayout =
	{
		("conference_room", "Conference Room", 0f),
		("corporate_office", "Corporate Office", 1600f),
		("coffee_shop", "Coffee Shop", 3200f),
		("printer_server_area", "Printer / Hallway", 4800f),
		("server_room", "Server Room", 6400f),
	};

	private static readonly Dictionary<string, int> RoomIndexById = new(StringComparer.Ordinal)
	{
		["conference_room"] = 0,
		["corporate_office"] = 1,
		["coffee_shop"] = 2,
		["printer_server_area"] = 3,
		["server_room"] = 4,
	};

	private const float RoomWidth = 1376f;
	private const float RoomHeight = 768f;

	private RoomManager _roomManager = null!;
	private AgentDirector _agentDirector = null!;
	private Node2D _ySortActors = null!;
	private Camera2D _camera = null!;
	private SierraConsole _sierraConsole = null!;
	private Label? _roomLabel;
	private Viewport? _viewport;

	private int _activeRoomIndex = 2; // CoffeeShop default
	private Vector2 _cameraTargetPos;
	private readonly Dictionary<string, NavigationRegion2D> _roomNavRegions = new(StringComparer.Ordinal);

	public override void _Ready()
	{
		_roomManager = GetNode<RoomManager>("/root/RoomManager");
		_agentDirector = GetNode<AgentDirector>("AgentDirector");
		_ySortActors = GetNode<Node2D>("OfficeWorld/YSortActors");
		_camera = GetNode<Camera2D>("OfficeWorld/Camera2D");
		_sierraConsole = GetNode<SierraConsole>("HUD/SierraConsole");
		_roomLabel = GetNodeOrNull<Label>("HUD/RoomNavBar/RoomLabel");
		_viewport = GetViewport();
		if (_viewport is not null)
		{
			_viewport.SizeChanged += OnViewportSizeChanged;
		}

		RegisterRoomsPortalsAndPois();
		SetupFurnitureUnderYSort();
		BakeRoomNavigationObstacles();
		SpawnAndSeedCharacters();
		WireRoomNavButtons();

		FocusRoom("coffee_shop", snapCamera: true);
		UpdateCameraZoom();
	}

	public override void _ExitTree()
	{
		if (_viewport is not null && GodotObject.IsInstanceValid(_viewport))
		{
			_viewport.SizeChanged -= OnViewportSizeChanged;
		}
	}

	public override void _Process(double delta)
	{
		_camera.Position = _camera.Position.Lerp(_cameraTargetPos, (float)Math.Min(1.0, delta * 10.0));
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (_sierraConsole.HasInputFocus)
		{
			return;
		}

		if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
		{
			return;
		}

		switch (keyEvent.Keycode)
		{
			case Key.Left:
				FocusRoomIndex(Math.Max(0, _activeRoomIndex - 1));
				break;
			case Key.Right:
				FocusRoomIndex(Math.Min(RoomLayout.Length - 1, _activeRoomIndex + 1));
				break;
			case Key.Key1:
				FocusRoomIndex(0);
				break;
			case Key.Key2:
				FocusRoomIndex(1);
				break;
			case Key.Key3:
				FocusRoomIndex(2);
				break;
			case Key.Key4:
				FocusRoomIndex(3);
				break;
			case Key.Key5:
				FocusRoomIndex(4);
				break;
		}
	}

	private void RegisterRoomsPortalsAndPois()
	{
		Node roomsRoot = GetNode("OfficeWorld/Rooms");
		foreach (var (roomId, _, xOffset) in RoomLayout)
		{
			_roomManager.RegisterRoomBounds(roomId, new Rect2(xOffset, 0f, 1376f, 768f));
		}

		foreach (Node roomNode in roomsRoot.GetChildren())
		{
			RegisterRecursive(roomNode);

			if (roomNode is Node2D room2D)
			{
				string? roomId = FindRoomIdByXOffset(room2D.Position.X);
				NavigationRegion2D? navRegion = roomNode.GetNodeOrNull<NavigationRegion2D>("NavigationRegion2D");
				if (roomId is not null && navRegion is not null)
				{
					_roomNavRegions[roomId] = navRegion;
				}

				if (roomId is not null && navRegion?.NavigationPolygon is { } navPoly)
				{
					Rect2 walkBounds = RoomManager.ComputeWalkBoundsFromPolygon(
						navPoly.GetVertices(),
						navRegion.GlobalPosition);
					if (walkBounds.Size.X > 0f && walkBounds.Size.Y > 0f)
					{
						_roomManager.RegisterRoomWalkBounds(roomId, walkBounds);
					}
				}
			}
		}
	}

	private static string? FindRoomIdByXOffset(float xOffset)
	{
		foreach (var (roomId, _, layoutX) in RoomLayout)
		{
			if (Mathf.Abs(xOffset - layoutX) < 1f)
			{
				return roomId;
			}
		}

		return null;
	}

	private void RegisterRecursive(Node node)
	{
		if (node is DoorPortal portal)
		{
			_roomManager.RegisterPortal(portal);
		}
		else if (node is POIMarker poi)
		{
			_roomManager.RegisterPoi(poi);
		}

		foreach (Node child in node.GetChildren())
		{
			RegisterRecursive(child);
		}
	}

	private void SetupFurnitureUnderYSort()
	{
		// Coffee shop counter (444x230) in CoffeeShop (x=3200)
		AddFurnitureSprite(
			"CoffeeCounter",
			"res://assets/sprites/coffee-shop-counter.png",
			new Vector2(3200f + 688f, 520f),
			roomId: "coffee_shop",
			colliderSize: new Vector2(430f, 55f),
			colliderOffset: new Vector2(0f, -27.5f));

		// 3x Developer desks (299x215) & rear-facing chairs in CorporateOffice (x=1600)
		AddFurnitureSprite(
			"Desk1",
			"res://assets/sprites/google-developer-desk.png",
			new Vector2(1600f + 350f, 535f),
			roomId: "corporate_office",
			colliderSize: new Vector2(270f, 52f),
			colliderOffset: new Vector2(0f, -34f));
		AddFurnitureSprite(
			"Desk2",
			"res://assets/sprites/google-developer-desk.png",
			new Vector2(1600f + 688f, 535f),
			roomId: "corporate_office",
			colliderSize: new Vector2(270f, 52f),
			colliderOffset: new Vector2(0f, -34f));
		AddFurnitureSprite(
			"Desk3",
			"res://assets/sprites/google-developer-desk.png",
			new Vector2(1600f + 1026f, 535f),
			roomId: "corporate_office",
			colliderSize: new Vector2(270f, 52f),
			colliderOffset: new Vector2(0f, -34f));
		AddFurnitureSprite("DeskChair1", "res://assets/sprites/conference-chair-back.png", new Vector2(1600f + 350f, 560f));
		AddFurnitureSprite("DeskChair2", "res://assets/sprites/conference-chair-back.png", new Vector2(1600f + 688f, 560f));
		AddFurnitureSprite("DeskChair3", "res://assets/sprites/conference-chair-back.png", new Vector2(1600f + 1026f, 560f));

		// Conference table (433x150) & chairs in ConferenceRoom (x=0)
		AddFurnitureSprite("ConfChairBack1", "res://assets/sprites/conference-chair-back.png", new Vector2(560f, 565f));
		AddFurnitureSprite("ConfChairBack2", "res://assets/sprites/conference-chair-back.png", new Vector2(816f, 565f));
		AddFurnitureSprite(
			"ConfTable",
			"res://assets/sprites/conference-table.png",
			new Vector2(688f, 540f),
			roomId: "conference_room",
			colliderSize: new Vector2(415f, 46f),
			colliderOffset: new Vector2(0f, -31f));
		AddFurnitureSprite("ConfChairFront1", "res://assets/sprites/conference-chair.png", new Vector2(560f, 470f));
		AddFurnitureSprite("ConfChairFront2", "res://assets/sprites/conference-chair.png", new Vector2(816f, 470f));

		// Printer cutout in PrinterServerArea (x=4800); printer is drawn in background at local x=325..590, y=555..594
		AddRoomObstacleCollider(
			"PrinterObstacle",
			roomId: "printer_server_area",
			worldRect: new Rect2(4800f + 325f, 555f, 265f, 39f));
	}

	private void AddRoomObstacleCollider(string nodeName, string roomId, Rect2 worldRect)
	{
		if (worldRect.Size.X <= 0f || worldRect.Size.Y <= 0f)
		{
			return;
		}

		var body = new StaticBody2D
		{
			Name = nodeName,
			Position = worldRect.GetCenter(),
			CollisionLayer = 1,
			CollisionMask = 0
		};
		var shape = new CollisionShape2D
		{
			Name = $"{nodeName}Shape",
			Shape = new RectangleShape2D { Size = worldRect.Size }
		};
		body.AddChild(shape);
		_ySortActors.AddChild(body);

		_roomManager.RegisterRoomObstacle(roomId, worldRect);
	}

	private void AddFurnitureSprite(
		string nodeName,
		string texturePath,
		Vector2 worldPos,
		string? roomId = null,
		Vector2? colliderSize = null,
		Vector2? colliderOffset = null)
	{
		Texture2D? tex = ResourceLoader.Load<Texture2D>(texturePath);
		if (tex is null)
		{
			return;
		}

		var sprite = new Sprite2D
		{
			Name = nodeName,
			Texture = tex,
			Centered = true,
			Offset = new Vector2(0f, -tex.GetHeight() * 0.5f),
			Position = worldPos
		};

		if (colliderSize is { } size && size.X > 0f && size.Y > 0f)
		{
			Vector2 offset = colliderOffset ?? new Vector2(0f, -size.Y * 0.5f);
			var body = new StaticBody2D
			{
				Name = $"{nodeName}Body",
				Position = offset,
				CollisionLayer = 1,
				CollisionMask = 0
			};
			var shape = new CollisionShape2D
			{
				Name = $"{nodeName}Shape",
				Shape = new RectangleShape2D { Size = size }
			};
			body.AddChild(shape);
			sprite.AddChild(body);

			if (!string.IsNullOrEmpty(roomId))
			{
				Rect2 worldRect = new Rect2(worldPos + offset - size * 0.5f, size);
				_roomManager.RegisterRoomObstacle(roomId, worldRect);
			}
		}

		_ySortActors.AddChild(sprite);
	}

	private void BakeRoomNavigationObstacles()
	{
		foreach (var (roomId, navRegion) in _roomNavRegions)
		{
			IReadOnlyList<Rect2> obstacles = _roomManager.GetRoomObstacles(roomId);
			if (obstacles.Count == 0 || navRegion.NavigationPolygon is not { } sourcePoly)
			{
				continue;
			}

			if (sourcePoly.Duplicate() is not NavigationPolygon navPoly)
			{
				continue;
			}

			var sourceGeometry = new NavigationMeshSourceGeometryData2D();
			int outlineCount = navPoly.GetOutlineCount();
			for (int i = 0; i < outlineCount; i++)
			{
				sourceGeometry.AddTraversableOutline(navPoly.GetOutline(i));
			}

			float agentRadius = navPoly.AgentRadius;
			// CharacterAgent's CollisionShape2D is a 16px-radius circle at (0, -12) relative to GlobalPosition
			// (so X extends +-16px, Y extends -28px above feet and +4px below feet).
			// Expand the navmesh obstruction outline beyond agentRadius so navmesh paths keep the
			// character's physics collider clear of furniture corners instead of snagging on them.
			float extraX = Mathf.Max(0f, 18f - agentRadius);
			float extraTop = Mathf.Max(0f, 6f - agentRadius);
			float extraBottom = Mathf.Max(0f, 30f - agentRadius);

			foreach (Rect2 obstacleWorldRect in obstacles)
			{
				Vector2 localPos = obstacleWorldRect.Position - navRegion.GlobalPosition;
				float left = localPos.X - extraX;
				float right = localPos.X + obstacleWorldRect.Size.X + extraX;
				float top = localPos.Y - extraTop;
				float bottom = localPos.Y + obstacleWorldRect.Size.Y + extraBottom;
				Vector2[] holeOutline =
				{
					new Vector2(left, top),
					new Vector2(right, top),
					new Vector2(right, bottom),
					new Vector2(left, bottom),
				};
				sourceGeometry.AddObstructionOutline(holeOutline);
			}

			NavigationServer2D.BakeFromSourceGeometryData(navPoly, sourceGeometry);
			navRegion.NavigationPolygon = navPoly;
		}
	}

	private void SpawnAndSeedCharacters()
	{
		PackedScene? charScene = ResourceLoader.Load<PackedScene>("res://scenes/CharacterAgent.tscn");
		if (charScene is null)
		{
			GD.PushError("[Main] Failed to load res://scenes/CharacterAgent.tscn");
			return;
		}

		Dictionary<string, string> seedMap = _roomManager.Graph.SeedCharacters(CharacterAgent.Roster);
		var spawnedByRoom = new Dictionary<string, List<Vector2>>(StringComparer.Ordinal);

		foreach (string charName in CharacterAgent.Roster)
		{
			CharacterAgent agent = charScene.Instantiate<CharacterAgent>();
			agent.Name = charName;
			agent.CharacterName = charName;
			_ySortActors.AddChild(agent);

			string startRoom = seedMap.TryGetValue(charName, out string? assigned) ? assigned : "coffee_shop";
			if (!spawnedByRoom.TryGetValue(startRoom, out var occupied))
			{
				occupied = new List<Vector2>();
				spawnedByRoom[startRoom] = occupied;
			}
			Vector2 spawnPos = _roomManager.GetRandomWalkPointInRoom(startRoom, occupied);
			occupied.Add(spawnPos);
			agent.InitializeInRoom(startRoom, spawnPos);
			_agentDirector.RegisterAgent(agent);
		}
	}

	private void WireRoomNavButtons()
	{
		Node? navBar = GetNodeOrNull("HUD/RoomNavBar");
		if (navBar is null)
		{
			return;
		}

		for (int i = 0; i < RoomLayout.Length; i++)
		{
			int idx = i;
			Button? btn = navBar.GetNodeOrNull<Button>($"BtnRoom{i + 1}");
			if (btn is not null)
			{
				btn.Pressed += () => FocusRoomIndex(idx);
			}
		}
	}

	public void FocusRoom(string roomId, bool snapCamera = false)
	{
		if (RoomIndexById.TryGetValue(roomId, out int idx))
		{
			FocusRoomIndex(idx, snapCamera);
		}
	}

	private void FocusRoomIndex(int index, bool snapCamera = false)
	{
		_activeRoomIndex = Math.Clamp(index, 0, RoomLayout.Length - 1);
		var (_, displayName, xOffset) = RoomLayout[_activeRoomIndex];
		_cameraTargetPos = new Vector2(xOffset + 688f, 384f);

		if (snapCamera)
		{
			_camera.Position = _cameraTargetPos;
		}

		if (_roomLabel is not null)
		{
			_roomLabel.Text = $"[{_activeRoomIndex + 1}/5] {displayName}";
		}
	}

	private void OnViewportSizeChanged()
	{
		UpdateCameraZoom();
	}

	private void UpdateCameraZoom()
	{
		if (_camera is null || _viewport is null)
		{
			return;
		}

		Vector2 viewportSize = _viewport.GetVisibleRect().Size;
		_camera.Zoom = ComputeCoverZoom(viewportSize);
	}

	public static Vector2 ComputeCoverZoom(Vector2 viewportSize, float contentWidth = RoomWidth, float contentHeight = RoomHeight)
	{
		if (!float.IsFinite(viewportSize.X) || !float.IsFinite(viewportSize.Y) ||
			!float.IsFinite(contentWidth) || !float.IsFinite(contentHeight) ||
			viewportSize.X <= 0f || viewportSize.Y <= 0f ||
			contentWidth <= 0f || contentHeight <= 0f)
		{
			return Vector2.One;
		}

		float zoom = Mathf.Max(viewportSize.X / contentWidth, viewportSize.Y / contentHeight);
		return new Vector2(zoom, zoom);
	}
}
