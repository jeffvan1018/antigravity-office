using Godot;

namespace AntigravityOffice;

public partial class DoorPortal : Node2D
{
    [Export]
    public string RoomId { get; set; } = string.Empty;

    [Export]
    public string PortalName { get; set; } = string.Empty;

    [Export]
    public string TargetRoomId { get; set; } = string.Empty;

    [Export]
    public string TargetPortalName { get; set; } = string.Empty;

    public Vector2 ArrivalPosition => GlobalPosition;
}
