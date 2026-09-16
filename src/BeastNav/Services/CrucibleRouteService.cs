using System.Linq;
using System.Numerics;
using BeastNav.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;

namespace BeastNav.Services;

/// <summary>
/// Walks the character through a recorded sequence of waypoints across a
/// Crucible board's own map (tile to tile), stopping to let combat happen
/// wherever a tile's area-change trigger fires.
/// </summary>
/// <remarks>
/// The board's tile triggers can't be detected from a distance (confirmed
/// via NearbyObjectDumpService: the EventObj that marks one only appears in
/// the object table once already within ~2y — contact range, too late to
/// walk toward), so this records real coordinates once per board instead of
/// reacting to anything. See <see cref="Configuration.CrucibleBoardRoutePresets"/>
/// for why this is considered safe to reintroduce despite an earlier,
/// similar feature being deleted for crash caution.
/// </remarks>
/// <remarks>
/// Each board can hold several named presets (e.g. alternate paths, or
/// revisions kept side by side while tuning one) — see
/// <see cref="Configuration.CrucibleBoardRoutePresets"/>. Only the one
/// named by <see cref="Configuration.SelectedRoutePresetByBoard"/> for the
/// current board is ever walked or recorded to at a time.
/// </remarks>
/// <remarks>
/// Yields to combat on its own — it never assumes AutoCombatEnabled is on.
/// Pauses whenever <see cref="ConditionFlag.InCombat"/> is set or any
/// targetable BattleNpc exists anywhere (no distance cap — see
/// <see cref="HasNearbyEnemy"/>), so it never tries to walk toward a
/// board-map waypoint while actually standing inside a combat sub-area (a
/// different physical space where those coordinates mean nothing).
/// </remarks>
/// <remarks>
/// Unattended movement — against the FFXIV ToS, self-responsibility. Off by
/// default. Records/clears/renames only the currently selected board and
/// preset; never touches another board's or another preset's.
/// </remarks>
public sealed class CrucibleRouteService
{
    private const float ArrivalRadius = 3f;

    // "イベント中" (confirmed against Lumina's Status sheet: LockActions and
    // LockControl are both set) — covers the board's own intro, and also
    // the fade-to-black each tile's area-change trigger plays. Route
    // movement must not touch navmesh during this: it's the actual signal
    // for "a scripted transition is happening right now", which BetweenAreas/
    // OccupiedInCutSceneEvent alone don't reliably cover for these in-duty
    // tile transitions the same way they do a full zone change.
    private const uint EventBuffStatusId = 1268;

    // Central Shroud (黒衣森: 中央森林) — where the entry NPC lives. Excluded
    // for the same reason CrucibleCombatService excludes it: CrucibleEntryService
    // drives its own navmesh movement to the NPC there, and this service must
    // never contend with it for control before an actual board is entered.
    private const uint CentralShroudTerritoryId = 148;

    private const string DefaultPresetName = "デフォルト";

    // How long to keep waiting after the last enemy disappears before
    // trusting that we're actually back on the board map. HasNearbyEnemy()
    // can clear the instant the last enemy on a tile dies, which is earlier
    // than the game's own area-change transition actually completes — acting
    // on that immediately sent the character walking (using board-map
    // coordinates) while still inside the old combat sub-area for a moment,
    // throwing off the final approach to the next waypoint just enough to
    // fall short of it.
    private static readonly TimeSpan PostCombatSettleDelay = TimeSpan.FromSeconds(3.5);

    private readonly Configuration configuration;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly NavmeshService navmesh;
    private readonly Action saveConfiguration;
    private readonly IPluginLog log;

    private int currentIndex;
    private bool wasNpcDetected;
    private DateTime? enemyGoneAtUtc;

    // Tracks whether *this service* currently has an active navmesh request
    // in flight, so pausing can cancel it (see Pause()) without ever calling
    // navmesh.Stop() when CrucibleCombatService is the one actually driving
    // movement — Stop() would cancel its in-flight approach too, since both
    // services share the same single NavmeshService.
    private bool isDrivingMovement;

    public CrucibleRouteService(
        Configuration configuration,
        IObjectTable objectTable,
        ICondition condition,
        IClientState clientState,
        NavmeshService navmesh,
        Action saveConfiguration,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.objectTable = objectTable;
        this.condition = condition;
        this.clientState = clientState;
        this.navmesh = navmesh;
        this.saveConfiguration = saveConfiguration;
        this.log = log;
    }

    /// <summary>Short human status for the main window.</summary>
    public string Status { get; private set; } = "off";

    /// <summary>Preset names saved for the currently selected board, in save order.</summary>
    public IReadOnlyList<string> PresetNames
        => this.configuration.CrucibleBoardRoutePresets.TryGetValue(this.configuration.SelectedCrucibleBoard, out var presets)
            ? presets.Select(p => p.Name).ToList()
            : [];

    /// <summary>The preset CrucibleRouteService is actually walking/recording to right now (falls back to the first preset found if the saved selection no longer exists).</summary>
    public string ActivePresetName => this.FindActivePreset()?.Name ?? string.Empty;

    public int WaypointCount => this.FindActivePreset()?.Waypoints.Count ?? 0;

    /// <summary>Switches which preset is active for the currently selected board, resetting any in-progress walk.</summary>
    public void SelectPreset(string name)
    {
        this.configuration.SelectedRoutePresetByBoard[this.configuration.SelectedCrucibleBoard] = name;
        this.currentIndex = 0;
        if (this.isDrivingMovement)
        {
            this.isDrivingMovement = false;
            this.navmesh.Stop();
        }

        this.saveConfiguration();
    }

    /// <summary>Creates a new, empty preset under the currently selected board and makes it active.</summary>
    public bool TryCreatePreset(string name, out string message)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            message = "名前を入力してください";
            return false;
        }

        var presets = this.GetOrCreatePresetList();
        if (presets.Any(p => p.Name == trimmed))
        {
            message = $"「{trimmed}」は既に存在します";
            return false;
        }

        presets.Add(new CrucibleRoutePreset { Name = trimmed, Waypoints = [] });
        this.SelectPreset(trimmed);

        message = $"{this.configuration.SelectedCrucibleBoard}: プリセット「{trimmed}」を新規作成しました";
        this.log.Information("[BeastHelper] Crucible route: created preset {Preset} for {Board}.", trimmed, this.configuration.SelectedCrucibleBoard);
        return true;
    }

    /// <summary>Renames the currently active preset.</summary>
    public bool TryRenameActivePreset(string newName, out string message)
    {
        var trimmed = newName.Trim();
        if (trimmed.Length == 0)
        {
            message = "名前を入力してください";
            return false;
        }

        var presets = this.GetOrCreatePresetList();
        var active = this.FindActivePreset();
        if (active is null)
        {
            message = "対象のプリセットがありません";
            return false;
        }

        if (presets.Any(p => p.Name == trimmed && p != active))
        {
            message = $"「{trimmed}」は既に存在します";
            return false;
        }

        var oldName = active.Name;
        active.Name = trimmed;
        this.configuration.SelectedRoutePresetByBoard[this.configuration.SelectedCrucibleBoard] = trimmed;
        this.saveConfiguration();

        message = $"{this.configuration.SelectedCrucibleBoard}: 「{oldName}」を「{trimmed}」に名前変更しました";
        this.log.Information("[BeastHelper] Crucible route: renamed preset {Old} -> {New} for {Board}.", oldName, trimmed, this.configuration.SelectedCrucibleBoard);
        return true;
    }

    /// <summary>Deletes the currently active preset (the board keeps any other presets it has).</summary>
    public bool TryDeleteActivePreset(out string message)
    {
        var presets = this.GetOrCreatePresetList();
        var active = this.FindActivePreset();
        if (active is null)
        {
            message = "対象のプリセットがありません";
            return false;
        }

        presets.Remove(active);
        this.configuration.SelectedRoutePresetByBoard.Remove(this.configuration.SelectedCrucibleBoard);
        this.currentIndex = 0;
        if (this.isDrivingMovement)
        {
            this.isDrivingMovement = false;
            this.navmesh.Stop();
        }

        this.saveConfiguration();

        message = $"{this.configuration.SelectedCrucibleBoard}: プリセット「{active.Name}」を削除しました";
        this.log.Information("[BeastHelper] Crucible route: deleted preset {Preset} for {Board}.", active.Name, this.configuration.SelectedCrucibleBoard);
        return true;
    }

    /// <summary>Call once per framework tick with CrucibleEntryService's current NpcDetected value — used to restart the route from the top on each fresh run.</summary>
    public void Tick(bool npcDetected)
    {
        if (npcDetected && !this.wasNpcDetected)
        {
            this.currentIndex = 0;
        }

        this.wasNpcDetected = npcDetected;

        if (!this.configuration.AutoEnterCrucibleEnabled || !this.configuration.AutoWalkRouteEnabled)
        {
            this.enemyGoneAtUtc = null;
            this.Pause("off");
            return;
        }

        if (this.clientState.TerritoryType == CentralShroudTerritoryId)
        {
            this.enemyGoneAtUtc = null;
            this.Pause("対象ゾーン外 (中央森林)");
            return;
        }

        var preset = this.FindActivePreset();
        if (preset is null || preset.Waypoints.Count == 0)
        {
            this.enemyGoneAtUtc = null;
            this.Pause($"ルート未記録: {this.configuration.SelectedCrucibleBoard}");
            return;
        }

        if (this.condition[ConditionFlag.BetweenAreas]
            || this.condition[ConditionFlag.BetweenAreas51]
            || this.condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            this.enemyGoneAtUtc = null;
            this.Pause("待機(エリア移動中)");
            return;
        }

        var player = this.objectTable.LocalPlayer;
        if (player is null)
        {
            return;
        }

        // Covers each tile's own fade-to-black transition — movement must
        // stay suspended through it and only resume once genuinely back.
        if (HasStatus(player, EventBuffStatusId))
        {
            this.enemyGoneAtUtc = null;
            this.Pause("待機(イベント中)");
            return;
        }

        if (this.condition[ConditionFlag.InCombat] || this.HasNearbyEnemy())
        {
            // Combat has priority — never fight CrucibleCombatService for
            // navmesh control, and never walk toward a board-map waypoint
            // while actually standing inside a combat sub-area. Pause()
            // only cancels a request *this* service issued, so an approach
            // CrucibleCombatService just started this same frame is left
            // untouched.
            this.enemyGoneAtUtc = null;
            this.Pause("待機(戦闘中)");
            return;
        }

        var now = DateTime.UtcNow;
        this.enemyGoneAtUtc ??= now;
        var settleRemaining = PostCombatSettleDelay - (now - this.enemyGoneAtUtc.Value);
        if (settleRemaining > TimeSpan.Zero)
        {
            this.Pause($"待機(戦闘後の安定待ち {settleRemaining.TotalSeconds:0.0}秒)");
            return;
        }

        var waypoints = preset.Waypoints;
        if (this.currentIndex >= waypoints.Count)
        {
            this.Pause("ルート完了");
            return;
        }

        var waypoint = waypoints[this.currentIndex];
        var target = new Vector3(waypoint.X, waypoint.Y, waypoint.Z);
        var distance = Vector3.Distance(player.Position, target);
        if (distance <= ArrivalRadius)
        {
            this.navmesh.Stop();
            this.isDrivingMovement = false;
            this.currentIndex++;
            this.Status = $"{preset.Name}: 地点{this.currentIndex}/{waypoints.Count}に到着";
            return;
        }

        this.navmesh.MoveCloseTo(target, false, ArrivalRadius);
        this.isDrivingMovement = true;
        this.Status = $"{preset.Name}: 移動中 地点{this.currentIndex + 1}/{waypoints.Count} ({distance:0.0}y)";
    }

    /// <summary>
    /// Enters (or stays in) a paused state, cancelling any navmesh request
    /// this service itself has in flight — but only that: if
    /// <see cref="isDrivingMovement"/> is already false (nothing of ours is
    /// running, e.g. CrucibleCombatService owns navmesh right now instead),
    /// this touches nothing.
    /// </summary>
    private void Pause(string status)
    {
        if (this.isDrivingMovement)
        {
            this.isDrivingMovement = false;
            this.navmesh.Stop();
        }

        this.Status = status;
    }

    /// <summary>Records the player's current position as the next waypoint on the currently active preset (creating a default one first if the board has none yet).</summary>
    public bool TryAddWaypointAtCurrentPosition(out string message)
    {
        var player = this.objectTable.LocalPlayer;
        if (player is null)
        {
            message = "プレイヤー未検出";
            return false;
        }

        var preset = this.FindActivePreset();
        if (preset is null)
        {
            preset = new CrucibleRoutePreset { Name = DefaultPresetName, Waypoints = [] };
            this.GetOrCreatePresetList().Add(preset);
            this.configuration.SelectedRoutePresetByBoard[this.configuration.SelectedCrucibleBoard] = preset.Name;
        }

        preset.Waypoints.Add(new CrucibleWaypoint { X = player.Position.X, Y = player.Position.Y, Z = player.Position.Z });
        this.saveConfiguration();

        message = $"{this.configuration.SelectedCrucibleBoard}/{preset.Name}: 地点{preset.Waypoints.Count}を記録 ({player.Position.X:0.0}, {player.Position.Y:0.0}, {player.Position.Z:0.0})";
        this.log.Information("[BeastHelper] Crucible route: recorded waypoint {Index} for {Board}/{Preset} at {Pos}.", preset.Waypoints.Count, this.configuration.SelectedCrucibleBoard, preset.Name, player.Position);
        return true;
    }

    /// <summary>Clears the currently active preset's waypoints only — the preset itself (and its name) stays.</summary>
    public void ClearActivePresetWaypoints()
    {
        var preset = this.FindActivePreset();
        if (preset is null)
        {
            return;
        }

        preset.Waypoints.Clear();
        this.currentIndex = 0;
        if (this.isDrivingMovement)
        {
            this.isDrivingMovement = false;
            this.navmesh.Stop();
        }

        this.saveConfiguration();
        this.log.Information("[BeastHelper] Crucible route: cleared waypoints for {Board}/{Preset}.", this.configuration.SelectedCrucibleBoard, preset.Name);
    }

    private List<CrucibleRoutePreset> GetOrCreatePresetList()
    {
        if (!this.configuration.CrucibleBoardRoutePresets.TryGetValue(this.configuration.SelectedCrucibleBoard, out var presets))
        {
            presets = [];
            this.configuration.CrucibleBoardRoutePresets[this.configuration.SelectedCrucibleBoard] = presets;
        }

        return presets;
    }

    private CrucibleRoutePreset? FindActivePreset()
    {
        if (!this.configuration.CrucibleBoardRoutePresets.TryGetValue(this.configuration.SelectedCrucibleBoard, out var presets) || presets.Count == 0)
        {
            return null;
        }

        var selectedName = this.configuration.SelectedRoutePresetByBoard.GetValueOrDefault(this.configuration.SelectedCrucibleBoard);
        return presets.FirstOrDefault(p => p.Name == selectedName) ?? presets[0];
    }

    private static bool HasStatus(IPlayerCharacter character, uint statusId)
    {
        foreach (var status in character.StatusList)
        {
            if (status.StatusId == statusId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True if any targetable BattleNpc exists anywhere — no distance cap.
    /// The board's own map has zero BattleNpcs on it at all (confirmed via
    /// NearbyObjectDumpService while standing far from any tile), so finding
    /// even one anywhere is a reliable "we're inside a combat sub-area now"
    /// signal. A distance-limited check here previously missed enemies that
    /// were simply more than ~30y away right after the area changed (a wide
    /// arena), and the route kept walking toward its board-map waypoint —
    /// which meant nothing in this different space and sent the character
    /// running toward whatever the raw coordinates happened to be, including
    /// off toward the arena's outer wall.
    /// </summary>
    private bool HasNearbyEnemy()
    {
        foreach (var obj in this.objectTable)
        {
            if (obj.ObjectKind == ObjectKind.BattleNpc && obj.IsTargetable)
            {
                return true;
            }
        }

        return false;
    }
}
