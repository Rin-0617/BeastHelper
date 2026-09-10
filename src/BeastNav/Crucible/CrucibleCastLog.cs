using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace BeastNav.Crucible;

/// <summary>
/// Watches the enemies in the Crucible and records every distinct
/// <c>(enemy, cast)</c> pair it sees to <c>crucible-casts.json</c> in the plugin
/// config folder. Purely observational — this is how the curated
/// <see cref="CrucibleEncounterDatabase"/> gets its raw material without anyone
/// hand-typing action ids.
/// </summary>
public sealed class CrucibleCastLog
{
    private const string FileName = "crucible-casts.json";
    private const string ObservationsFileName = "crucible-observations.jsonl";

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly JsonSerializerOptions compactJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Dictionary<(uint Enemy, uint Action), CastRecord> records = [];
    private readonly HashSet<(ulong GameObjectId, uint Action)> castsInProgress = [];

    private bool dirty;
    private DateTime lastFlush = DateTime.MinValue;

    public CrucibleCastLog(IDalamudPluginInterface pluginInterface, IDataManager dataManager, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.dataManager = dataManager;
        this.log = log;
        this.Load();
    }

    public string FilePath => Path.Combine(this.pluginInterface.ConfigDirectory.FullName, FileName);

    /// <summary>Append-only geometry log, one JSON object per cast instance — the raw material for the dodge solver, ideal to fill from replays.</summary>
    public string ObservationsPath => Path.Combine(this.pluginInterface.ConfigDirectory.FullName, ObservationsFileName);

    public int RecordCount => this.records.Count;

    /// <summary>Call once per framework tick with the latest snapshot.</summary>
    public void Observe(CrucibleState state)
    {
        if (!state.InCrucible)
        {
            this.castsInProgress.Clear();
            return;
        }

        var stillCasting = new HashSet<(ulong, uint)>();

        foreach (var enemy in state.Enemies)
        {
            if (!enemy.IsCasting || enemy.CastActionId == 0)
            {
                continue;
            }

            var live = (enemy.GameObjectId, enemy.CastActionId);
            stillCasting.Add(live);

            // Only fold a cast in once per continuous cast, not every frame.
            if (!this.castsInProgress.Add(live))
            {
                continue;
            }

            this.Record(state.TerritoryId, enemy);
            this.AppendObservation(state, enemy);
        }

        this.castsInProgress.IntersectWith(stillCasting);

        var now = DateTime.UtcNow;
        if (this.dirty && now - this.lastFlush >= FlushInterval)
        {
            this.Flush();
        }
    }

    private void Record(uint territoryId, CrucibleEnemy enemy)
    {
        var key = (enemy.NameId, enemy.CastActionId);
        var (castType, effectRange, omenId) = this.DescribeAction(enemy.CastActionId);
        var actionName = this.ResolveActionName(enemy.CastActionId);

        if (this.records.TryGetValue(key, out var existing))
        {
            this.records[key] = existing with
            {
                Count = existing.Count + 1,
                TargetsPlayer = existing.TargetsPlayer || enemy.CastTargetsPlayer,
                CastSeconds = enemy.CastTotal > 0 ? enemy.CastTotal : existing.CastSeconds,
            };
        }
        else
        {
            this.records[key] = new CastRecord
            {
                EnemyNameId = enemy.NameId,
                EnemyName = enemy.Name,
                ActionId = enemy.CastActionId,
                ActionName = actionName,
                CastType = castType,
                EffectRange = effectRange,
                OmenId = omenId,
                CastSeconds = enemy.CastTotal,
                TargetsPlayer = enemy.CastTargetsPlayer,
                TerritoryId = territoryId,
                FirstSeen = DateTime.UtcNow.ToString("o"),
                Count = 1,
            };

            this.log.Information(
                "[BeastHelper] Crucible: new cast observed — {Enemy} → {Action} (id {Id}, castType {Type}, range {Range}).",
                enemy.Name,
                actionName,
                enemy.CastActionId,
                castType,
                effectRange);
        }

        this.dirty = true;
    }

    public void Flush()
    {
        if (!this.dirty)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(this.pluginInterface.ConfigDirectory.FullName);
            var file = new CastFile(
                "Observed enemy casts in the Crucible (闘獣練). Auto-generated; safe to delete.",
                this.records.Values
                    .OrderBy(record => record.TerritoryId)
                    .ThenBy(record => record.EnemyNameId)
                    .ThenBy(record => record.ActionId)
                    .ToList());
            File.WriteAllText(this.FilePath, JsonSerializer.Serialize(file, this.jsonOptions));
            this.dirty = false;
            this.lastFlush = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[BeastHelper] Failed to write {File}.", FileName);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(this.FilePath))
            {
                return;
            }

            var file = JsonSerializer.Deserialize<CastFile>(File.ReadAllText(this.FilePath), this.jsonOptions);
            foreach (var record in file?.Casts ?? [])
            {
                if (record.ActionId == 0)
                {
                    continue;
                }

                this.records[(record.EnemyNameId, record.ActionId)] = record;
            }

            this.log.Information("[BeastHelper] Loaded {Count} observed Crucible casts.", this.records.Count);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[BeastHelper] Failed to read {File}.", FileName);
        }
    }

    private (byte CastType, byte EffectRange, uint OmenId) DescribeAction(uint actionId)
    {
        try
        {
            var row = this.dataManager.GetExcelSheet<LuminaAction>()?.GetRowOrDefault(actionId);
            return row is null
                ? ((byte)0, (byte)0, 0u)
                : (row.Value.CastType, row.Value.EffectRange, row.Value.Omen.RowId);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private string ResolveActionName(uint actionId)
    {
        try
        {
            var name = this.dataManager.GetExcelSheet<LuminaAction>()?.GetRowOrDefault(actionId)?.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? $"Action #{actionId}" : name!;
        }
        catch
        {
            return $"Action #{actionId}";
        }
    }

    private void AppendObservation(CrucibleState state, CrucibleEnemy enemy)
    {
        try
        {
            var (castType, effectRange, omenId) = this.DescribeAction(enemy.CastActionId);
            var obs = new ObservationRecord
            {
                T = DateTime.UtcNow.ToString("o"),
                TerritoryId = state.TerritoryId,
                EnemyNameId = enemy.NameId,
                EnemyName = enemy.Name,
                ActionId = enemy.CastActionId,
                ActionName = this.ResolveActionName(enemy.CastActionId),
                CastType = castType,
                EffectRange = effectRange,
                OmenId = omenId,
                CastTotal = enemy.CastTotal,
                TargetsPlayer = enemy.CastTargetsPlayer,
                EnemyPos = [enemy.Position.X, enemy.Position.Y, enemy.Position.Z],
                EnemyRot = enemy.Rotation,
                PlayerPos = [state.PlayerPosition.X, state.PlayerPosition.Y, state.PlayerPosition.Z],
                PlayerRot = state.PlayerRotation,
            };

            Directory.CreateDirectory(this.pluginInterface.ConfigDirectory.FullName);
            File.AppendAllText(this.ObservationsPath, JsonSerializer.Serialize(obs, this.compactJson) + "\n");
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Failed to append a Crucible observation.");
        }
    }

    private sealed record CastFile(string Comment, List<CastRecord> Casts);

    private sealed record ObservationRecord
    {
        public string T { get; init; } = string.Empty;

        public uint TerritoryId { get; init; }

        public uint EnemyNameId { get; init; }

        public string EnemyName { get; init; } = string.Empty;

        public uint ActionId { get; init; }

        public string ActionName { get; init; } = string.Empty;

        public byte CastType { get; init; }

        public byte EffectRange { get; init; }

        public uint OmenId { get; init; }

        public float CastTotal { get; init; }

        public bool TargetsPlayer { get; init; }

        public float[] EnemyPos { get; init; } = [];

        public float EnemyRot { get; init; }

        public float[] PlayerPos { get; init; } = [];

        public float PlayerRot { get; init; }
    }

    public sealed record CastRecord
    {
        public uint EnemyNameId { get; init; }

        public string EnemyName { get; init; } = string.Empty;

        public uint ActionId { get; init; }

        public string ActionName { get; init; } = string.Empty;

        public byte CastType { get; init; }

        public byte EffectRange { get; init; }

        public uint OmenId { get; init; }

        public float CastSeconds { get; init; }

        public bool TargetsPlayer { get; init; }

        public uint TerritoryId { get; init; }

        public string FirstSeen { get; init; } = string.Empty;

        public int Count { get; init; }
    }
}
