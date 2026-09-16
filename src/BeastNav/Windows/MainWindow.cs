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
    private readonly BossModDodgeService bossModDodge;
    private readonly CrucibleBoardService crucibleBoard;
    private readonly CrucibleReadyCheckService crucibleReadyCheck;
    private readonly CrucibleCombatService crucibleCombat;
    private readonly CrucibleRecoveryItemService crucibleRecoveryItem;
    private readonly CrucibleLootService crucibleLoot;
    private readonly CrucibleResultService crucibleResult;
    private readonly CrucibleTreasureService crucibleTreasure;
    private readonly CrucibleShopService crucibleShop;
    private readonly CrucibleLoopService crucibleLoop;
    private readonly CrucibleEntryService crucibleEntry;
    private readonly IGameGui gameGui;
    private readonly Action<BeastDestination> teleport;
    private readonly Action saveConfiguration;
    private readonly Action syncBeastNote;
    private readonly Action<bool> setBossModDodgeEnabled;
    private string filter = string.Empty;
    private int selectedIndex;

    public MainWindow(
        Configuration configuration,
        BeastDataService beastData,
        BeastDestinationService destinations,
        NavmeshService navmesh,
        BeastTamingStateService tamingState,
        BossModDodgeService bossModDodge,
        CrucibleBoardService crucibleBoard,
        CrucibleReadyCheckService crucibleReadyCheck,
        CrucibleCombatService crucibleCombat,
        CrucibleRecoveryItemService crucibleRecoveryItem,
        CrucibleLootService crucibleLoot,
        CrucibleResultService crucibleResult,
        CrucibleTreasureService crucibleTreasure,
        CrucibleShopService crucibleShop,
        CrucibleLoopService crucibleLoop,
        CrucibleEntryService crucibleEntry,
        IGameGui gameGui,
        Action<BeastDestination> teleport,
        Action saveConfiguration,
        Action syncBeastNote,
        Action<bool> setBossModDodgeEnabled)
        : base($"BeastHelper {VersionLabel}###BeastHelperMain")
    {
        this.configuration = configuration;
        this.beastData = beastData;
        this.destinations = destinations;
        this.navmesh = navmesh;
        this.tamingState = tamingState;
        this.bossModDodge = bossModDodge;
        this.crucibleBoard = crucibleBoard;
        this.crucibleReadyCheck = crucibleReadyCheck;
        this.crucibleCombat = crucibleCombat;
        this.crucibleRecoveryItem = crucibleRecoveryItem;
        this.crucibleLoot = crucibleLoot;
        this.crucibleResult = crucibleResult;
        this.crucibleTreasure = crucibleTreasure;
        this.crucibleShop = crucibleShop;
        this.crucibleLoop = crucibleLoop;
        this.crucibleEntry = crucibleEntry;
        this.gameGui = gameGui;
        this.teleport = teleport;
        this.saveConfiguration = saveConfiguration;
        this.syncBeastNote = syncBeastNote;
        this.setBossModDodgeEnabled = setBossModDodgeEnabled;
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
        if (!ImGui.BeginTabBar("##BeastHelperTabs"))
        {
            return;
        }

        if (ImGui.BeginTabItem("図鑑"))
        {
            this.DrawControls();
            ImGui.Separator();
            this.DrawPetList();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("闘獣練"))
        {
            this.DrawCrucibleTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
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

    private void DrawCrucibleTab()
    {
        ImGui.TextWrapped("闘獣練 (Crucible of the Unbroken) 向けの実験的機能です。");
        ImGui.TextWrapped("盤内のマスからマスへの移動は自動化できません。手動で移動してください。");
        ImGui.Spacing();

        ImGui.TextUnformatted("自動化");
        ImGui.Separator();

        var dodgeEnabled = this.configuration.BossModDodgeEnabled;
        if (ImGui.Checkbox("BMR回避連携", ref dodgeEnabled))
        {
            this.setBossModDodgeEnabled(dodgeEnabled);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "BossModRebornの移動専用モジュール(NormalMovement)をIPC経由でON/OFFします。\n"
                + "戦闘技の選択には一切触れません、移動のみBMRに任せます。\n"
                + "対象のコンテンツにBMRのモジュールが無い場合は何も起きません。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"BMR module: {(this.bossModDodge.HasActiveModule ? "yes" : "no")} / {this.bossModDodge.Status}");

        var autoEnter = this.configuration.AutoEnterCrucibleEnabled;
        if (ImGui.Checkbox("自動突入", ref autoEnter))
        {
            this.configuration.AutoEnterCrucibleEnabled = autoEnter;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "ONの間、中央森林の入口NPC(ラウダ)へ自動で近づいて話しかけ、\n"
                + "参加確認(突入)、盤内の宝箱画面(XBMContentsTreasure、常に1番目の\n"
                + "アイテムを選択)、ショップ画面(XBMContentsItemShop、何も買わずに終了)、\n"
                + "戦闘後の結果画面(XBMResult)の「次のページ」/「退出」、戦利品画面\n"
                + "(XBMContentsBooty)の「すべて入手」とその確認ダイアログまで、\n"
                + "一連の流れを自動で進めます。\n"
                + "呼び笛(戦闘直前の3体選択)・魔獣編成・ルート・コンテンツ内容の選択・\n"
                + "「戦闘開始」ボタンには触れません(手動で行ってください)。盤面選択\n"
                + "(XBMStageList)は下の別チェックで分けています(過去に2回クラッシュ\n"
                + "した経緯があるため)。\n"
                + "注意: 参加確認(ContentsFinderConfirm)はゲーム共通の画面なので、ONの間は\n"
                + "闘獣練以外のコンテンツの参加確認も自動で「突入」してしまいます。");
        }

        var loopCount = this.configuration.CrucibleLoopCount;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("周回回数(0=無制限)", ref loopCount))
        {
            this.configuration.CrucibleLoopCount = Math.Max(0, loopCount);
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "突入NPCを再度検知できたタイミングを「1周完了」の区切りとして数え、\n"
                + "指定した回数に達したら自動突入を自動でOFFに戻します。\n"
                + "最初にNPCを見つけるまでは1周目の最中として扱うので、そこではまだ\n"
                + "カウントされません(2回目の検知で1周完了になります)。\n"
                + "0なら従来通り無制限に周回し続けます。1なら1回きりで周回しません。\n"
                + "カウントは自動突入を手動でONにするたびリセットされます。");
        }

        ImGui.Indent();
        ImGui.TextDisabled($"突入: {this.crucibleEntry.Status}");
        ImGui.TextDisabled($"突入確認: {this.crucibleReadyCheck.Status}");
        ImGui.TextDisabled($"結果画面: {this.crucibleResult.Status}");
        ImGui.TextDisabled($"宝箱: {this.crucibleTreasure.Status}");
        ImGui.TextDisabled($"ショップ: {this.crucibleShop.Status}");
        ImGui.TextDisabled($"戦利品: {this.crucibleLoot.Status}");
        ImGui.TextDisabled($"周回: {this.crucibleLoop.Status}");
        ImGui.Unindent();

        ImGui.Spacing();

        var autoSelectBoard = this.configuration.AutoSelectBoardEnabled;
        if (ImGui.Checkbox("盤面を自動選択", ref autoSelectBoard))
        {
            this.configuration.AutoSelectBoardEnabled = autoSelectBoard;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "盤面選択画面(XBMStageList)が開いたら、下で選んだ盤面を自動でクリック\n"
                + "します。表示されてから2秒待ってから選択します。\n"
                + "警告: この自動選択は過去に2回、実際のゲームクラッシュ\n"
                + "(AgentXBMStageDetailList.Update内)の原因とほぼ断定された機能です。\n"
                + "2秒待機は再発防止のための対策ですが、原因がタイミングだったと\n"
                + "確証があるわけではありません。ONにする場合は自己責任で慎重に。");
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        if (ImGui.BeginCombo("盤面##crucibleBoard", this.configuration.SelectedCrucibleBoard))
        {
            foreach (var boardName in this.configuration.CrucibleBoardScripts.Keys)
            {
                var isSelected = boardName == this.configuration.SelectedCrucibleBoard;
                if (ImGui.Selectable(boardName, isSelected))
                {
                    this.configuration.SelectedCrucibleBoard = boardName;
                    this.saveConfiguration();
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled($"盤面: {this.crucibleBoard.Status}");

        ImGui.Spacing();

        var autoCombat = this.configuration.AutoCombatEnabled;
        if (ImGui.Checkbox("戦闘自動化 (RSR連携)", ref autoCombat))
        {
            this.configuration.AutoCombatEnabled = autoCombat;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "近くに敵を見つけたら1体に狙いを定めて接近し(倒れるまでその敵を\n"
                + "追い続けます — 毎回「一番近い敵」を選び直す方式は動作が不安定だった\n"
                + "ため、AutoDutyの移動処理を参考に1体固定に変更しました)、射程内に\n"
                + "入ったらRotationSolverRebornのAutoモードを「/rotation Auto」でONにして\n"
                + "戦闘を任せます。戦闘に入ったら接近は止め、以降はRSRに任せます。敵が\n"
                + "いなくなったら自動でOFFに戻します。低HP時の「ひきつけろ」強制発動は\n"
                + "不安定だったため外したままです。技・ローテーション・ポジショナルの\n"
                + "選択には一切触れません(すべてRSR側に任せます)。RSR未導入の場合は\n"
                + "戦闘の自動切替は何も起きません。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{this.crucibleCombat.Status} / RSR: {(this.crucibleCombat.RsrReportsActive ? "active" : "inactive")}");

        ImGui.Spacing();

        var autoRecoveryItem = this.configuration.AutoRecoveryItemEnabled;
        if (ImGui.Checkbox("回復薬を自動使用", ref autoRecoveryItem))
        {
            this.configuration.AutoRecoveryItemEnabled = autoRecoveryItem;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "戦闘中、自身のHPが下の閾値を下回ったら闘獣練の「魔獣回復薬」を自動で\n"
                + "使用します(通常のポーション類ではなく、コンテンツ内配布の専用アイテム)。\n"
                + "所持している中で使用可能な最上位のものを自動選択します。\n"
                + "ネイティブ関数のシグネチャ検索に失敗した場合は自動的に無効化されます。");
        }

        var recoveryThreshold = this.configuration.AutoRecoveryItemHpThreshold;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("HP閾値(%)##recoveryItem", ref recoveryThreshold, 1f, 100f, "%.0f"))
        {
            this.configuration.AutoRecoveryItemHpThreshold = Math.Clamp(recoveryThreshold, 1f, 100f);
            this.saveConfiguration();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(this.crucibleRecoveryItem.Status);

        ImGui.TextDisabled("魔獣編成(呼び笛)の自動化は今のところ未対応です(手動で設定してください)。");
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
