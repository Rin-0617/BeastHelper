namespace BeastNav.Models;

/// <summary>A named, reusable sequence of waypoints for one Crucible board.</summary>
public sealed class CrucibleRoutePreset
{
    public string Name { get; set; } = string.Empty;

    public List<CrucibleWaypoint> Waypoints { get; set; } = [];
}
