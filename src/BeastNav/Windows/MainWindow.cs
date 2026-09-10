using System.Numerics;
using System.Reflection;
using BeastNav.Crucible;
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
    private readonly CrucibleStateReader crucible;
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
        CrucibleStateReader crucible,
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
        this.crucible = crucible;
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

        this.DrawCrucibleControls();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Filter by name, No, or description", ref this.filter, 256);
    }

    private void DrawCrucibleControls()
    {
        var enabled = this.configuration.CrucibleOverlayEnabled;
        if (ImGui.Checkbox("闘獣練アシスト (Crucible assist)", ref enabled))
        {
            this.configuration.CrucibleOverlayEnabled = enabled;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "闘獣練の状態を読み取り、推奨行動をオーバーレイ表示します（自動操作はしません）。\n"
                + "オーバーレイは闘獣練の中でのみ表示されます。");
        }

        ImGui.SameLine();
        var pin = this.configuration.CrucibleOverlayAlwaysShow;
        if (ImGui.Checkbox("常に表示", ref pin))
        {
            this.configuration.CrucibleOverlayAlwaysShow = pin;
            this.saveConfiguration();
        }

        ImGui.SameLine();
        var paint = this.configuration.CrucibleZonePaint;
        if (ImGui.Checkbox("危険範囲を画面に描画", ref paint))
        {
            this.configuration.CrucibleZonePaint = paint;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("危険範囲と、ソルバが計算した退避方向の矢印を画面上に描きます（ドライラン：動きません）。");
        }

        ImGui.SameLine();
        var dodge = this.configuration.CrucibleAutoDodge;
        if (ImGui.Checkbox("自動回避 (実験的)", ref dodge))
        {
            this.configuration.CrucibleAutoDodge = dodge;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "詠唱中、計算した安全地点へ自動でキャラを移動させます。\n"
                + "戦闘中の自動移動は FFXIV 規約違反です。自己責任で。\n"
                + "BeastHelper の目的地移動中は作動しません。");
        }

        ImGui.SameLine();
        var autoCombat = this.configuration.CrucibleAutoCombat;
        if (ImGui.Checkbox("自動戦闘 (実験的)", ref autoCombat))
        {
            this.configuration.CrucibleAutoCombat = autoCombat;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "最寄りの敵をターゲットし、アクションバー1の使える技を自動で撃ちます。\n"
                + "戦闘の自動化は FFXIV 規約違反です。自己責任で。\n"
                + "回避中は戦闘より回避を優先します。");
        }

        ImGui.SameLine();
        var state = this.crucible.Current;
        var status = state.InCrucible
            ? $"闘獣練: 検出 ({state.DetectionSource}) · 敵 {state.Enemies.Count}"
            : "闘獣練: 未検出";
        ImGui.TextDisabled(status);
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

        // The plugin decides whether this needs a teleport first or just a move
        // in the current zone; either way it runs the same mount + ready gate.
        this.teleport(destination);
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
