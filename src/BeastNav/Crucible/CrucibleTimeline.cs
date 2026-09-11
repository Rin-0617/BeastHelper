using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace BeastNav.Crucible;

/// <summary>
/// Learns each enemy type's cast script — elapsed seconds since it was first
/// engaged → which action it uses — from what has actually been observed, and
/// predicts the next few seconds of casts for enemies currently on screen.
/// Crucible packs are scripted and repeat the same monsters fight after fight,
/// so once a script is known it applies the moment that monster type shows up
/// again, even before its cast bar appears.
/// </summary>
/// <remarks>
/// This does not read anything about the enemy's internal AI — it is a timeline
/// built purely from prior observation (the same idea community raid-timeline
/// tools use), keyed by <c>NameId</c> so it carries over between pulls of the
/// same monster and persists across sessions.
/// </remarks>
public sealed class CrucibleTimeline
{
    private const string FileName = "crucible-timeline.json";

    // How far ahead a predicted cast is surfaced, and how close two observed
    // timings for the same action have to be to count as "the same beat".
    private const float LookaheadSeconds = 2.5f;
    private const float MergeToleranceSeconds = 1f;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly JsonSerializerOptions json = new() { WriteIndented = true };

    private readonly Dictionary<uint, List<ScriptEntry>> script = [];
    private readonly Dictionary<ulong, DateTime> firstSeen = [];
    private readonly Dictionary<ulong, HashSet<uint>> firedForInstance = [];
    private readonly HashSet<(ulong GameObjectId, uint Action)> castsInProgress = [];

    private bool dirty;

    public CrucibleTimeline(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.Load();
    }

    public int LearnedEnemyCount => this.script.Count;

    /// <summary>Call once per framework tick with the latest snapshot.</summary>
    public void Observe(CrucibleState state)
    {
        if (!state.InCrucible)
        {
            this.firstSeen.Clear();
            this.firedForInstance.Clear();
            this.castsInProgress.Clear();
            return;
        }

        var now = DateTime.UtcNow;
        var seen = new HashSet<ulong>();

        foreach (var enemy in state.Enemies)
        {
            seen.Add(enemy.GameObjectId);
            if (!this.firstSeen.ContainsKey(enemy.GameObjectId))
            {
                this.firstSeen[enemy.GameObjectId] = now;
            }

            if (!enemy.IsCasting || enemy.CastActionId == 0)
            {
                continue;
            }

            var live = (enemy.GameObjectId, enemy.CastActionId);
            if (!this.castsInProgress.Add(live))
            {
                continue; // already logged this cast instance
            }

            this.MarkFired(enemy.GameObjectId, enemy.CastActionId);
            var elapsed = (float)(now - this.firstSeen[enemy.GameObjectId]).TotalSeconds;
            this.Learn(enemy.NameId, elapsed, enemy.CastActionId);
        }

        this.castsInProgress.RemoveWhere(key =>
            state.Enemies.FirstOrDefault(e => e.GameObjectId == key.GameObjectId) is not { IsCasting: true } e
            || e.CastActionId != key.Action);

        foreach (var stale in this.firstSeen.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            this.firstSeen.Remove(stale);
            this.firedForInstance.Remove(stale);
        }

        if (this.dirty)
        {
            this.Save();
        }
    }

    /// <summary>
    /// Casts the script says are due for currently-visible enemies within the
    /// next couple of seconds, that haven't actually fired yet this pull.
    /// </summary>
    public IReadOnlyList<PredictedCast> Predict(CrucibleState state)
    {
        var results = new List<PredictedCast>();
        if (!state.InCrucible)
        {
            return results;
        }

        var now = DateTime.UtcNow;
        foreach (var enemy in state.Enemies)
        {
            if (enemy.CurrentHp == 0 || enemy.IsCasting)
            {
                continue; // the live cast-bar path already covers this one
            }

            if (!this.script.TryGetValue(enemy.NameId, out var entries) || !this.firstSeen.TryGetValue(enemy.GameObjectId, out var start))
            {
                continue;
            }

            var elapsed = (float)(now - start).TotalSeconds;
            var fired = this.firedForInstance.GetValueOrDefault(enemy.GameObjectId);

            foreach (var entry in entries)
            {
                if (fired?.Contains(entry.ActionId) == true)
                {
                    continue;
                }

                var until = entry.Elapsed - elapsed;
                if (until is > 0f and <= LookaheadSeconds)
                {
                    results.Add(new PredictedCast(enemy, entry.ActionId, until));
                }
            }
        }

        return results;
    }

    private void MarkFired(ulong gameObjectId, uint actionId)
    {
        if (!this.firedForInstance.TryGetValue(gameObjectId, out var set))
        {
            set = [];
            this.firedForInstance[gameObjectId] = set;
        }

        set.Add(actionId);
    }

    private void Learn(uint enemyNameId, float elapsed, uint actionId)
    {
        if (!this.script.TryGetValue(enemyNameId, out var entries))
        {
            entries = [];
            this.script[enemyNameId] = entries;
        }

        // Already have this action's beat (within tolerance, or at all) — the
        // first clean reading is trusted; don't let a later pull's timing drift
        // overwrite it.
        if (entries.Any(e => e.ActionId == actionId))
        {
            return;
        }

        entries.Add(new ScriptEntry(elapsed, actionId));
        this.dirty = true;
        this.log.Debug("[BeastHelper] Crucible timeline: enemy {Enemy} → action {Action} at +{Elapsed:0.0}s.", enemyNameId, actionId, elapsed);
    }

    private void Load()
    {
        try
        {
            var path = this.FilePath();
            if (!File.Exists(path))
            {
                return;
            }

            var saved = JsonSerializer.Deserialize<List<SavedEnemyScript>>(File.ReadAllText(path)) ?? [];
            foreach (var s in saved)
            {
                this.script[s.EnemyNameId] = s.Entries.Select(e => new ScriptEntry(e.Elapsed, e.ActionId)).ToList();
            }

            if (this.script.Count > 0)
            {
                this.log.Information("[BeastHelper] Loaded Crucible timelines for {Count} enemy types.", this.script.Count);
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[BeastHelper] Failed to load {File}.", FileName);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(this.pluginInterface.ConfigDirectory.FullName);
            var saved = this.script
                .Select(kv => new SavedEnemyScript(kv.Key, kv.Value.Select(e => new SavedEntry(e.Elapsed, e.ActionId)).ToList()))
                .ToList();
            File.WriteAllText(this.FilePath(), JsonSerializer.Serialize(saved, this.json));
            this.dirty = false;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[BeastHelper] Failed to save {File}.", FileName);
        }
    }

    private string FilePath() => Path.Combine(this.pluginInterface.ConfigDirectory.FullName, FileName);

    private readonly record struct ScriptEntry(float Elapsed, uint ActionId);

    private sealed record SavedEntry(float Elapsed, uint ActionId);

    private sealed record SavedEnemyScript(uint EnemyNameId, List<SavedEntry> Entries);
}

/// <summary>An upcoming cast the timeline expects but that hasn't started yet.</summary>
public readonly record struct PredictedCast(CrucibleEnemy Enemy, uint ActionId, float SecondsUntilCast);
