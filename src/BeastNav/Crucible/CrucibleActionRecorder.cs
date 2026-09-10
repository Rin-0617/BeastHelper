using System.Text.Json;
using BeastNav.Services;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace BeastNav.Crucible;

/// <summary>
/// Records every action the player successfully uses while in the Crucible to
/// <c>crucible-my-actions.json</c>. Crucible combat actions (beast skills, the
/// content-only taunt / provoke) can't be put on a hotbar, so this is how we
/// learn their ids before wiring rule-based usage.
/// </summary>
public sealed unsafe class CrucibleActionRecorder : IDisposable
{
    private const string FileName = "crucible-my-actions.json";

    private delegate bool UseActionDelegate(
        ActionManager* manager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        uint mode,
        uint comboRouteId,
        bool* outOptAreaTargeted);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IDataManager dataManager;
    private readonly CrucibleStateReader reader;
    private readonly IPluginLog log;
    private readonly JsonSerializerOptions json = new() { WriteIndented = true };

    private readonly Dictionary<(byte Type, uint Id), ActionRecord> records = [];
    private readonly Hook<UseActionDelegate>? hook;

    private bool dirty;

    public CrucibleActionRecorder(
        IDalamudPluginInterface pluginInterface,
        IGameInteropProvider interop,
        IDataManager dataManager,
        CrucibleStateReader reader,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.dataManager = dataManager;
        this.reader = reader;
        this.log = log;
        this.Load();

        try
        {
            this.hook = interop.HookFromAddress<UseActionDelegate>(
                (nint)ActionManager.MemberFunctionPointers.UseAction, this.UseActionDetour);
            this.hook.Enable();
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[BeastHelper] Could not hook UseAction; Crucible action recording disabled.");
        }
    }

    public string FilePath => Path.Combine(this.pluginInterface.ConfigDirectory.FullName, FileName);

    public int Count => this.records.Count;

    public void Flush()
    {
        if (!this.dirty)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(this.pluginInterface.ConfigDirectory.FullName);
            var ordered = this.records.Values.OrderByDescending(r => r.Count).ToList();
            File.WriteAllText(this.FilePath, JsonSerializer.Serialize(ordered, this.json));
            this.dirty = false;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[BeastHelper] Failed to write {File}.", FileName);
        }
    }

    public void Dispose()
    {
        this.Flush();
        this.hook?.Dispose();
    }

    private bool UseActionDetour(
        ActionManager* manager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        uint mode,
        uint comboRouteId,
        bool* outOptAreaTargeted)
    {
        var used = this.hook!.Original(manager, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);

        try
        {
            if (used && actionId != 0 && this.reader.Current.InCrucible)
            {
                this.Record((byte)actionType, actionId);
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible action record failed.");
        }

        return used;
    }

    private void Record(byte type, uint id)
    {
        var key = (type, id);
        if (this.records.TryGetValue(key, out var existing))
        {
            this.records[key] = existing with { Count = existing.Count + 1 };
        }
        else
        {
            var name = this.ResolveName(id);
            this.records[key] = new ActionRecord
            {
                ActionType = type,
                ActionId = id,
                Name = name,
                FirstSeen = DateTime.UtcNow.ToString("o"),
                Count = 1,
            };

            this.log.Information("[BeastHelper] Crucible action observed: {Name} (type {Type}, id {Id}).", name, type, id);
        }

        this.dirty = true;
    }

    private string ResolveName(uint id)
    {
        try
        {
            var name = this.dataManager.GetExcelSheet<LuminaAction>()?.GetRowOrDefault(id)?.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? $"Action #{id}" : name!;
        }
        catch
        {
            return $"Action #{id}";
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

            var list = JsonSerializer.Deserialize<List<ActionRecord>>(File.ReadAllText(this.FilePath)) ?? [];
            foreach (var r in list.Where(r => r.ActionId != 0))
            {
                this.records[(r.ActionType, r.ActionId)] = r;
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[BeastHelper] Failed to read {File}.", FileName);
        }
    }

    public sealed record ActionRecord
    {
        public byte ActionType { get; init; }

        public uint ActionId { get; init; }

        public string Name { get; init; } = string.Empty;

        public string FirstSeen { get; init; } = string.Empty;

        public int Count { get; init; }
    }
}
