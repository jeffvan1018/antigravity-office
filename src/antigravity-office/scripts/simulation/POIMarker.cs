using Godot;

namespace AntigravityOffice;

public partial class POIMarker : Node2D
{
    [Export]
    public string RoomId { get; set; } = string.Empty;

    [Export]
    public string PoiKind { get; set; } = "DeskPOI";

    [Export]
    public string AnimationName { get; set; } = "idle";

    [Export]
    public bool FlipH { get; set; }

    [Export]
    public Vector2 SeatOffset { get; set; } = Vector2.Zero;

    public string? OccupiedBy { get; private set; }

    public bool IsAvailable => string.IsNullOrEmpty(OccupiedBy);

    public Vector2 TargetGlobalPosition => GlobalPosition + SeatOffset;

    public bool TryClaim(string characterId)
    {
        if (!IsAvailable && OccupiedBy != characterId)
        {
            return false;
        }

        OccupiedBy = characterId;
        return true;
    }

    public void Release(string characterId)
    {
        if (OccupiedBy == characterId)
        {
            OccupiedBy = null;
        }
    }

    public void ForceRelease()
    {
        OccupiedBy = null;
    }
}
