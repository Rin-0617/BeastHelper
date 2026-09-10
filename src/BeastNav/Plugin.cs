using BeastNav.Crucible;
using BeastNav.Services;
using BeastNav.Windows;
using BeastNav.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace BeastNav;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/beasthelper";
    private const string LegacyCommandName = "/beastnav";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chat;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly WindowSystem windowSystem = new("BeastHelper");
    private readonly Configuration configuration;
    private readonly BeastDataService beastData;
    private readonly BeastDestinationService destinations;
    private readonly NavmeshService navmesh;
    private readonly TeleportService teleport;
    private readonly MountService mount;
    private readonly BeastTamingStateService tamingState;
    private readonly CrucibleStateReader crucibleReader;
    private readonly CrucibleDecisionEngine crucibleEngine;
    private readonly CrucibleOverlay crucibleOverlay;
    private readonly MainWindow mainWindow;
    private readonly DebugWindow debugWindow;
    private BeastDestination? pendingDestination;
    private DateTime? pendingSettleUntil;
    private DateTime? pendingMountGiveUp;
    private DateTime? pendingMoveDeadline;
    private DateTime? nextMountAttempt;
    private DateTime nextAutoNoteSync = DateTime.MinValue;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IChatGui chat,
        IClientState clientState,
        ICondition condition,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IGameGui gameGui,
        IDataManager dataManager,
        IAetheryteList aetherytes,
        IFramework framework,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.chat = chat;
        this.clientState = clientState;
        this.condition = condition;
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.framework = framework;
        this.log = log;
        this.configuration = this.pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Historical bug: the config's default addon-name list was appended to
        // (not replaced) on every load, so it grew by five entries per session.
        this.configuration.BeastNoteAddonCandidates =
            this.configuration.BeastNoteAddonCandidates.Distinct().ToList();
        this.beastData = new BeastDataService(dataManager, log);
        this.destinations = new BeastDestinationService(pluginInterface, dataManager, this.beastData, log);
        this.navmesh = new NavmeshService(pluginInterface, condition, log);
        this.teleport = new TeleportService(aetherytes, log);
        this.mount = new MountService(log);
        this.tamingState = new BeastTamingStateService(this.gameGui, this.beastData, this.configuration, log);

        // Crucible (闘獣練) assist: read-only state → rule-based advice → overlay.
        // Nothing in this chain acts on the game or drives movement.
        var crucibleEncounters = new CrucibleEncounterDatabase(this.beastData, dataManager, log);
        this.crucibleReader = new CrucibleStateReader(
            this.clientState, this.condition, this.objectTable, targetManager, dataManager, this.beastData, log);
        this.crucibleEngine = new CrucibleDecisionEngine(
            new CrucibleMechanicDetector(crucibleEncounters),
            new CrucibleBeastSelector(this.beastData, crucibleEncounters));
        this.crucibleOverlay = new CrucibleOverlay(this.configuration, this.crucibleReader, this.crucibleEngine);

        this.clientState.TerritoryChanged += this.OnTerritoryChanged;
        framework.Update += this.OnFrameworkUpdate;

        this.mainWindow = new MainWindow(
            this.configuration,
            this.beastData,
            this.destinations,
            this.navmesh,
            this.tamingState,
            this.crucibleReader,
            this.clientState,
            this.gameGui,
            this.TeleportToDestination,
            this.SaveConfiguration,
            this.SyncBeastNote);

        // The main window always starts closed. It is only shown on an explicit
        // /beasthelper, the title-bar / config button, or the plugin installer.
        this.debugWindow = new DebugWindow(this.tamingState);

        this.windowSystem.AddWindow(this.mainWindow);
        this.windowSystem.AddWindow(this.debugWindow);
        this.windowSystem.AddWindow(this.crucibleOverlay);
        this.pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        this.pluginInterface.UiBuilder.OpenConfigUi += this.ToggleMainWindow;
        this.pluginInterface.UiBuilder.OpenMainUi += this.ToggleMainWindow;

        this.commandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open BeastHelper. Subcommands: sync, autosync, dumpnote, debug, reload, stop, crucible.",
        });
        this.commandManager.AddHandler(LegacyCommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Alias for /beasthelper.",
        });
    }

    public void Dispose()
    {
        this.clientState.TerritoryChanged -= this.OnTerritoryChanged;
        this.framework.Update -= this.OnFrameworkUpdate;
        this.commandManager.RemoveHandler(CommandName);
        this.commandManager.RemoveHandler(LegacyCommandName);
        this.pluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.ToggleMainWindow;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.ToggleMainWindow;
        this.windowSystem.RemoveAllWindows();
        this.SaveConfiguration();
    }

    private void OnCommand(string command, string arguments)
    {
        var args = arguments.Trim();
        if (args.Length == 0)
        {
            this.ToggleMainWindow();
            return;
        }

        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "sync":
                this.SyncBeastNote();
                break;
            case "autosync":
                this.configuration.AutoSyncBeastNote = !this.configuration.AutoSyncBeastNote;
                this.nextAutoNoteSync = DateTime.MinValue;
                this.SaveConfiguration();
                this.chat.Print($"[BeastHelper] 図鑑 auto-sync: {this.configuration.AutoSyncBeastNote}");
                break;
            case "dumpnote":
                this.tamingState.DumpNoteAddons();
                this.chat.Print("[BeastHelper] Dumped monster-note addon values to the plugin log (/xllog).");
                break;
            case "debug":
                this.debugWindow.IsOpen = !this.debugWindow.IsOpen;
                break;
            case "reload":
                this.beastData.Reload();
                this.destinations.Reload();
                this.chat.Print($"[BeastHelper] Reloaded {this.beastData.Pets.Count} pets, {this.destinations.ManualCount} manual destinations, and {this.destinations.BuiltInCount} built-in destinations.");
                break;
            case "stop":
                this.pendingDestination = null;
                this.navmesh.Stop();
                break;
            case "crucible":
                this.HandleCrucibleCommand(parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty);
                break;
            case "resetnote":
                this.configuration.TamedPetRowIds = [];
                this.nextAutoNoteSync = DateTime.MinValue;
                this.SaveConfiguration();
                this.chat.Print("[BeastHelper] Cleared the captured-monster list. Auto-sync will refill it from XBMManager.");
                break;
            default:
                this.chat.Print("[BeastHelper] Usage: /beasthelper [sync|autosync|dumpnote|debug|reload|stop|crucible|resetnote]");
                break;
        }
    }

    private void HandleCrucibleCommand(string sub)
    {
        switch (sub)
        {
            case "probe":
                this.crucibleReader.LogUnlockProbe(this.configuration.TamedPetRowIds);
                this.chat.Print("[BeastHelper] Crucible unlock probe written to the plugin log (/xllog).");
                break;
            case "pin":
                this.configuration.CrucibleOverlayAlwaysShow = !this.configuration.CrucibleOverlayAlwaysShow;
                this.SaveConfiguration();
                this.chat.Print($"[BeastHelper] Crucible overlay always-show: {this.configuration.CrucibleOverlayAlwaysShow}");
                break;
            default:
                this.configuration.CrucibleOverlayEnabled = !this.configuration.CrucibleOverlayEnabled;
                this.SaveConfiguration();
                this.chat.Print($"[BeastHelper] Crucible assist overlay: {this.configuration.CrucibleOverlayEnabled}");
                break;
        }
    }

    private void SyncBeastNote()
    {
        var result = this.tamingState.TrySync();
        if (result.Success)
        {
            this.SaveConfiguration();
            this.chat.Print($"[BeastHelper] {result.Message}");
        }
        else
        {
            this.chat.PrintError($"[BeastHelper] 図鑑 sync failed. {result.Message}");
            this.log.Information("[BeastHelper] 図鑑 sync diagnostics:\n{Diagnostics}", string.Join("\n", result.Diagnostics));
        }
    }

    private void ToggleMainWindow()
        => this.mainWindow.IsOpen = !this.mainWindow.IsOpen;

    private void TeleportToDestination(BeastDestination destination)
    {
        var now = DateTime.UtcNow;

        if (this.clientState.TerritoryType == destination.TerritoryId)
        {
            // Already in the right zone: no teleport and no settle wait, but
            // still run the shared mount + navmesh-ready gate before moving so
            // the behaviour matches the post-teleport path.
            this.pendingDestination = destination;
            this.pendingSettleUntil = null;
            this.pendingMountGiveUp = now.AddSeconds(12);
            this.pendingMoveDeadline = now.AddSeconds(20);
            this.nextMountAttempt = null;
            return;
        }

        this.pendingDestination = destination;
        this.pendingSettleUntil = null;
        this.pendingMountGiveUp = null;
        this.pendingMoveDeadline = null;
        this.nextMountAttempt = null;
        if (!this.teleport.TryTeleport(destination.TerritoryId))
        {
            this.pendingDestination = null;
            this.gameGui.OpenMapWithMapLink(destination.TerritoryId, destination.MapId, destination.Position);
        }
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        if (this.pendingDestination is not { } destination || destination.TerritoryId != territoryId)
        {
            return;
        }

        var now = DateTime.UtcNow;
        this.pendingSettleUntil = now.AddSeconds(2);
        this.pendingMountGiveUp = now.AddSeconds(14);
        this.pendingMoveDeadline = now.AddSeconds(45);
        this.nextMountAttempt = null;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.UpdatePendingMove();

        if (this.objectTable.LocalPlayer is { } player)
        {
            this.navmesh.Update(player.Position);
        }

        this.crucibleReader.Update();
        this.TryAutoSyncBeastNote();
    }

    private void UpdatePendingMove()
    {
        if (this.pendingDestination is not { } destination)
        {
            return;
        }

        // Not in the destination zone yet: the teleport is still in flight (or
        // being cast). Touching vnavmesh here would move the character and
        // cancel the teleport cast, stranding us in the wrong zone.
        if (this.clientState.TerritoryType != destination.TerritoryId)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var force = this.pendingMoveDeadline is { } deadline && now >= deadline;

        // Post-teleport settle window: the freshly loaded zone's navmesh may
        // still be streaming in, and moving too early fails with
        // "failed to find polygon on a mesh". Applies even when the mount was
        // kept through the teleport (FFXIV keeps you mounted).
        if (!force && this.pendingSettleUntil is { } settleUntil && now < settleUntil)
        {
            return;
        }

        // Hold until the player is back in control and vnavmesh has a navmesh for
        // the current zone, otherwise the pathfind has nothing to work with.
        if (!force && (this.condition[ConditionFlag.BetweenAreas]
            || this.condition[ConditionFlag.BetweenAreas51]
            || this.condition[ConditionFlag.OccupiedInCutSceneEvent]
            || !this.navmesh.IsReady()))
        {
            return;
        }

        var isMounted = this.condition[ConditionFlag.Mounted] || this.condition[ConditionFlag.RidingPillion];

        // Flight needs a mount; ground travel does not, so only stall for a
        // mount when the user asked to fly.
        var needMount = this.configuration.Fly && !isMounted;
        var canStillMount = !force && this.pendingMountGiveUp is { } giveUp && now < giveUp;

        if (needMount && canStillMount)
        {
            if (this.nextMountAttempt is not { } next || now >= next)
            {
                var accepted = this.mount.TryMountRoulette();
                this.nextMountAttempt = now.AddSeconds(accepted ? 10 : 3);
                if (!accepted)
                {
                    this.log.Information("[BeastHelper] Mount Roulette not usable yet; will retry.");
                }
            }

            return;
        }

        if (needMount)
        {
            this.log.Information("[BeastHelper] Could not mount in time; moving on foot.");
        }

        this.BeginPendingMove(destination);
    }

    private void TryAutoSyncBeastNote()
    {
        if (!this.configuration.AutoSyncBeastNote || !this.clientState.IsLoggedIn)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now < this.nextAutoNoteSync)
        {
            return;
        }

        this.nextAutoNoteSync = now.AddSeconds(5);

        var result = this.tamingState.TrySync(allowUnverifiedMerge: false);
        if (result.Synced)
        {
            this.SaveConfiguration();
            this.log.Information("[BeastHelper] {Message}", result.Message);
        }
    }

    private void BeginPendingMove(BeastDestination destination)
    {
        this.pendingDestination = null;
        this.pendingSettleUntil = null;
        this.pendingMountGiveUp = null;
        this.pendingMoveDeadline = null;
        this.nextMountAttempt = null;
        this.navmesh.MoveCloseTo(destination.Position, this.configuration.Fly, destination.Radius > 0 ? destination.Radius : this.configuration.StopDistance);
    }

    private void SaveConfiguration()
        => this.pluginInterface.SavePluginConfig(this.configuration);
}
