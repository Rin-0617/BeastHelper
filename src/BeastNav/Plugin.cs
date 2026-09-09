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
    private readonly MainWindow mainWindow;
    private readonly DebugWindow debugWindow;
    private BeastDestination? pendingDestination;
    private DateTime? mountAfterTerritoryChange;
    private DateTime? mountTimeout;
    private bool mountRequested;
    private DateTime nextAutoNoteSync = DateTime.MinValue;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IChatGui chat,
        IClientState clientState,
        ICondition condition,
        IObjectTable objectTable,
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
        this.beastData = new BeastDataService(dataManager, log);
        this.destinations = new BeastDestinationService(pluginInterface, dataManager, this.beastData, log);
        this.navmesh = new NavmeshService(pluginInterface, log, chat);
        this.teleport = new TeleportService(aetherytes, log);
        this.mount = new MountService(log);
        this.tamingState = new BeastTamingStateService(this.gameGui, this.beastData, this.configuration, log);
        this.clientState.TerritoryChanged += this.OnTerritoryChanged;
        framework.Update += this.OnFrameworkUpdate;

        this.mainWindow = new MainWindow(
            this.configuration,
            this.beastData,
            this.destinations,
            this.navmesh,
            this.tamingState,
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
        this.pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        this.pluginInterface.UiBuilder.OpenConfigUi += this.ToggleMainWindow;
        this.pluginInterface.UiBuilder.OpenMainUi += this.ToggleMainWindow;

        this.commandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open BeastHelper. Subcommands: sync, autosync, dumpnote, debug, reload, stop.",
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
                this.navmesh.Stop();
                break;
            default:
                this.chat.Print("[BeastHelper] Usage: /beasthelper [sync|autosync|dumpnote|debug|reload|stop]");
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
        if (this.clientState.TerritoryType == destination.TerritoryId)
        {
            this.navmesh.MoveCloseTo(destination.Position, this.configuration.Fly, destination.Radius > 0 ? destination.Radius : this.configuration.StopDistance);
            return;
        }

        this.pendingDestination = destination;
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

        this.mountAfterTerritoryChange = DateTime.UtcNow.AddSeconds(2);
        this.mountTimeout = null;
        this.mountRequested = false;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (this.pendingDestination is { } destination)
        {
            var now = DateTime.UtcNow;
            var isMounted = this.condition[ConditionFlag.Mounted] || this.condition[ConditionFlag.RidingPillion];
            if (isMounted)
            {
                this.BeginPendingMove(destination);
            }
            else if (!this.mountRequested && this.mountAfterTerritoryChange is { } mountAt && now >= mountAt)
            {
                this.mountRequested = this.mount.TryMountRoulette();
                this.mountTimeout = now.AddSeconds(8);
            }
            else if (this.mountRequested && this.mountTimeout is { } timeout && now >= timeout)
            {
                this.BeginPendingMove(destination);
            }
        }

        if (this.objectTable.LocalPlayer is { } player)
        {
            this.navmesh.CheckArrival(player.Position);
        }

        this.TryAutoSyncBeastNote();
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
        this.mountAfterTerritoryChange = null;
        this.mountTimeout = null;
        this.mountRequested = false;
        this.navmesh.MoveCloseTo(destination.Position, this.configuration.Fly, destination.Radius > 0 ? destination.Radius : this.configuration.StopDistance);
    }

    private void SaveConfiguration()
        => this.pluginInterface.SavePluginConfig(this.configuration);
}
