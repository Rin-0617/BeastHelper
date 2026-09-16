namespace BeastNav.Models;

/// <summary>A single recorded world-space point along a Crucible board's tile-to-tile walking route.</summary>
public sealed class CrucibleWaypoint
{
    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }
}
