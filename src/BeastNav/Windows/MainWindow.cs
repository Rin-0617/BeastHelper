using System.Numerics;
using System.Reflection;
using BeastNav.Models;
using BeastNav.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

namespace BeastNav.Windows;

public sealed class MainWindow : Window
{
    private const string OfuseUrl = "https://ofuse.me/rin0617";

    private static readonly string VersionLabel =
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"v{v.Major}.{v.Minor}.{v.Build}"
            : "dev";

    private readonly Configuration configuration;
    private readonly BeastDataService beastData;
    private readonly BeastDestinationService destinations;
    private readonly NavmeshService navmesh;
    private readonly BeastTamingStateService tamingState;
    private readonly IClientState clientState;
    private readonly IGameGui gameGui;
    private readonly Action<BeastDestination> teleport;
    private readonly Action saveConfiguration;
    private readonly Action syncBeastNote;
    private string filter = string.Empty;
    private int selectedIndex;

    public MainWindow(
        Configuration configuration,
        BeastDataService beastData,
        BeastDestinationService destinations,
        NavmeshService navmesh,
        BeastTamingStateService tamingState,
        IClientState clientState,
        IGameGui gameGui,
        Action<BeastDestination> teleport,
        Action saveConfiguration,
        Action syncBeastNote)
        : base($"BeastHelper {VersionLabel}###BeastHelperMain")
    {
        this.configuration = configuration;
        this.beastData = beastData;
        this.destinations = destinations;
        this.navmesh = navmesh;
        this.tamingState = tamingState;
        this.clientState = clientState;
        this.gameGui = gameGui;
        this.teleport = teleport;
        this.saveConfiguration = saveConfiguration;
        this.syncBeastNote = syncBeastNote;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 430),
            MaximumSize = new Vector2(1400, 1000),
        };
        this.RespectCloseHotkey = true;

        this.TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Heart,
            IconOffset = new Vector2(2f, 1f),
            Click = _ => Util.OpenLink(OfuseUrl),
            ShowTooltip = () => ImGui.SetTooltip($"OFUSE で作者を応援する\n{OfuseUrl}"),
        });
    }

    public override void Draw()
    {
        this.DrawControls();
        ImGui.Separator();
        this.DrawPetList();
    }

    private void DrawControls()
    {
        if (ImGui.Button("Reload"))
        {
            this.beastData.Reload();
            this.destinations.Reload();
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop"))
        {
            this.navmesh.Stop();
        }

        ImGui.SameLine();
        if (ImGui.Button("Sync 図鑑"))
        {
            this.syncBeastNote();
        }

        ImGui.SameLine();
        var autoSync = this.configuration.AutoSyncBeastNote;
        if (ImGui.Checkbox("Auto-sync 図鑑", ref autoSync))
        {
            this.configuration.AutoSyncBeastNote = autoSync;
            this.saveConfiguration();
        }

        ImGui.SameLine();
        var hideCompleted = this.configuration.HideCompletedPets;
        if (ImGui.Checkbox("捕獲済み・不要を隠す", ref hideCompleted))
        {
            this.configuration.HideCompletedPets = hideCompleted;
            this.saveConfiguration();
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(
            $"Captured: {this.configuration.TamedPetRowIds.Count}/{this.beastData.Pets.Count}");

        var lastSync = this.tamingState.LastResult;
        if (lastSync != BeastTamingSyncResult.NotRun && !string.IsNullOrWhiteSpace(lastSync.Message))
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"({lastSync.Message})");
        }

        var fly = this.configuration.Fly;
        if (ImGui.Checkbox("Fly", ref fly))
        {
            this.configuration.Fly = fly;
            this.saveConfiguration();
        }

        ImGui.SameLine();
        var range = this.configuration.StopDistance;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Stop distance", ref range, 1f, 30f, "%.1f"))
        {
            this.configuration.StopDistance = range;
            this.saveConfiguration();
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(this.navmesh.IsReady() ? "vnavmesh: ready" : "vnavmesh: unavailable");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Filter by name, No, or description", ref this.filter, 256);
    }

    private void DrawPetList()
    {
        var rows = this.FilteredPets().ToArray();
        if (rows.Length == 0)
        {
            ImGui.TextUnformatted("No XBMPet rows loaded.");
            return;
        }

        if (this.selectedIndex >= rows.Length)
        {
            this.selectedIndex = rows.Length - 1;
        }

        if (ImGui.BeginTable("##beasthelperPets", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("No", ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.9f);
            ImGui.TableSetupColumn("Dest", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Stats", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableHeadersRow();

            for (var i = 0; i < rows.Length; i++)
            {
                var pet = rows[i];
                var destination = this.destinations.Find(pet.PetRowId);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(pet.XbmRowId.ToString());

                ImGui.TableSetColumnIndex(1);
                var captured = this.configuration.TamedPetRowIds.Contains(pet.PetRowId);
                var label = captured ? $"[捕] {pet.Name}" : pet.Name;
                if (ImGui.Selectable($"{label}##{pet.PetRowId}", i == this.selectedIndex))
                {
                    this.selectedIndex = i;
                }

                if (captured && ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("図鑑に登録済み (captured)");
                }

                if (ImGui.BeginPopupContextItem($"##petmenu{pet.PetRowId}"))
                {
                    using (var disabled = ImRaiiDisabled(destination is null))
                    {
                        if (ImGui.MenuItem("Move"))
                        {
                            this.MoveTo(pet, destination);
                        }

                        if (ImGui.MenuItem("Open map"))
                        {
                            this.OpenMap(destination);
                        }
                    }

                    ImGui.EndPopup();
                }

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(destination is null ? "-" : DestinationLabel(destination));

                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted($"{pet.Stat1}/{pet.Stat2}/{pet.Stat3}/{pet.Stat4}/{pet.Stat5}");

                ImGui.TableSetColumnIndex(4);
                {
                    using var disabled = ImRaiiDisabled(destination is null || !CanNavigate(destination));
                    if (ImGui.SmallButton($"Move##move{pet.PetRowId}"))
                    {
                        this.MoveTo(pet, destination);
                    }

                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Map##map{pet.PetRowId}"))
                    {
                        this.OpenMap(destination);
                    }
                }

                ImGui.SameLine();
                var marked = this.configuration.MarkedPetRowIds.Contains(pet.PetRowId);
                if (ImGui.SmallButton(marked ? $"Show##pet{pet.PetRowId}" : $"Pet##pet{pet.PetRowId}"))
                {
                    if (marked)
                    {
                        this.configuration.MarkedPetRowIds.Remove(pet.PetRowId);
                    }
                    else
                    {
                        this.configuration.MarkedPetRowIds.Add(pet.PetRowId);
                    }

                    this.saveConfiguration();
                }
            }

            ImGui.EndTable();
        }

        this.DrawSelected(rows[this.selectedIndex]);
    }


    private void DrawSelected(BeastPet pet)
    {
        ImGui.Separator();
        ImGui.TextUnformatted($"{pet.Name}  No:{pet.XbmRowId}  Icon:{pet.IconId}");
        var destination = this.destinations.Find(pet.PetRowId);
        if (destination is not null)
        {
            ImGui.TextWrapped($"Dest: {DestinationLabel(destination)}");
        }

        if (!string.IsNullOrWhiteSpace(pet.Description))
        {
            ImGui.TextWrapped(pet.Description);
        }
    }

    private IEnumerable<BeastPet> FilteredPets()
    {
        var needle = this.filter.Trim();
        return this.beastData.Pets.Where(pet =>
            (!this.configuration.HideCompletedPets || !this.IsCompleted(pet)) &&
            (needle.Length == 0 ||
             pet.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
             pet.Description.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
             pet.XbmRowId.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase)));
    }

    private bool IsCompleted(BeastPet pet)
        => this.configuration.TamedPetRowIds.Contains(pet.PetRowId)
           || this.configuration.MarkedPetRowIds.Contains(pet.PetRowId);

    private void MoveTo(BeastPet pet, BeastDestination? destination)
    {
        if (destination is null || !CanNavigate(destination))
        {
            return;
        }

        if (this.clientState.TerritoryType != destination.TerritoryId)
        {
            this.teleport(destination);
            return;
        }

        var range = MathF.Max(1f, destination.Radius > 0 ? destination.Radius : this.configuration.StopDistance);
        this.navmesh.MoveCloseTo(destination.Position, this.configuration.Fly, range);
    }

    private void OpenMap(BeastDestination? destination)
    {
        if (destination is null || !CanNavigate(destination))
        {
            return;
        }

        this.gameGui.OpenMapWithMapLink(destination.TerritoryId, destination.MapId, destination.Position);
    }

    private static string DestinationLabel(BeastDestination destination)
    {
        if (!string.IsNullOrWhiteSpace(destination.Label))
        {
            return destination.Label;
        }

        if (destination.MapX is not null && destination.MapY is not null)
        {
            return $"Map {destination.MapX:0.0}, {destination.MapY:0.0}";
        }

        return $"T:{destination.TerritoryId} ({destination.X:0.0}, {destination.Y:0.0}, {destination.Z:0.0})";
    }

    private static bool CanNavigate(BeastDestination destination)
        => destination.TerritoryId != 0 && destination.MapId != 0;

    private static IDisposable ImRaiiDisabled(bool disabled)
    {
        if (disabled)
        {
            ImGui.BeginDisabled();
            return new ActionDisposable(ImGui.EndDisabled);
        }

        return ActionDisposable.Empty;
    }

    private sealed class ActionDisposable : IDisposable
    {
        public static readonly ActionDisposable Empty = new(null);
        private readonly Action? action;

        public ActionDisposable(Action? action)
        {
            this.action = action;
        }

        public void Dispose()
            => this.action?.Invoke();
    }
}
