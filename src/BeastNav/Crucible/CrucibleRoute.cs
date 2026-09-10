using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace BeastNav.Crucible;

/// <summary>
/// A recorded path through a Crucible stage. Record one by walking the stage
/// once with recording on; the autopilot then follows it between packs.
/// Persisted per territory as <c>crucible-route-&lt;id&gt;.json</c>.
/// </summary>
public sealed class CrucibleRoute
{
    // Drop a waypoint when the player has moved this far from the last one.
    private const float RecordSpacing = 4f;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly JsonSerializerOptions json = new() { WriteIndented = true };

    private uint loadedTerritory;
    private List<Vector3> waypoints = [];

    public CrucibleRoute(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public bool Recording { get; private set; }

    public int Count => this.waypoints.Count;

    public IReadOnlyList<Vector3> Waypoints => this.waypoints;

    public string PathFor(uint territoryId)
        => Path.Combine(this.pluginInterface.ConfigDirectory.FullName, $"crucible-route-{territoryId}.json");

    /// <summary>Loads the stored route for the territory if it isn't already loaded.</summary>
    public void EnsureLoaded(uint territoryId)
    {
        if (this.loadedTerritory == territoryId && !this.Recording)
        {
            return;
        }

        this.loadedTerritory = territoryId;
        this.waypoints = [];

        try
        {
            var file = this.PathFor(territoryId);
            if (File.Exists(file))
            {
                var points = JsonSerializer.Deserialize<List<float[]>>(File.ReadAllText(file)) ?? [];
                this.waypoints = points
                    .Where(p => p.Length == 3)
                    .Select(p => new Vector3(p[0], p[1], p[2]))
                    .ToList();
                this.log.Information("[BeastHelper] Loaded {Count} Crucible route waypoints for {Territory}.", this.waypoints.Count, territoryId);
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[BeastHelper] Failed to load Crucible route for {Territory}.", territoryId);
        }
    }

    public void ToggleRecording(uint territoryId, Vector3 playerPosition)
    {
        this.Recording = !this.Recording;
        if (this.Recording)
        {
            this.loadedTerritory = territoryId;
            this.waypoints = [playerPosition];
        }
        else
        {
            this.Save(territoryId);
        }
    }

    public void Sample(Vector3 playerPosition)
    {
        if (!this.Recording)
        {
            return;
        }

        if (this.waypoints.Count == 0
            || Vector3.Distance(this.waypoints[^1], playerPosition) >= RecordSpacing)
        {
            this.waypoints.Add(playerPosition);
        }
    }

    private void Save(uint territoryId)
    {
        try
        {
            Directory.CreateDirectory(this.pluginInterface.ConfigDirectory.FullName);
            var data = this.waypoints.Select(p => new[] { p.X, p.Y, p.Z }).ToList();
            File.WriteAllText(this.PathFor(territoryId), JsonSerializer.Serialize(data, this.json));
            this.log.Information("[BeastHelper] Saved {Count} Crucible route waypoints for {Territory}.", data.Count, territoryId);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[BeastHelper] Failed to save Crucible route for {Territory}.", territoryId);
        }
    }
}
