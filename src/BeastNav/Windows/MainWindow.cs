using System.Globalization;
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
    private readonly CruciblePetPartyService cruciblePetParty;
    private readonly CrucibleAutoDutyIpcService crucibleAutoDuty;
    private readonly CrucibleShopService crucibleShop;
    private readonly CrucibleLoopService crucibleLoop;
    private readonly CrucibleEntryService crucibleEntry;
    private readonly IGameGui gameGui;
    private readonly Action<BeastDestination> teleport;
    private readonly Action saveConfiguration;
    private readonly Action syncBeastNote;
    private readonly Action<bool> setBossModDodgeEnabled;
    private const string NoTreasureCategoryLabel = "(未設定)";

    // Display label -> matched keyword (see CrucibleTreasureService, which
    // just does an ordered plain-substring match against
    // Configuration.CrucibleTreasureKeywords) for the priority dropdowns
    // below. Keywords are plain substrings confirmed against real treasure
    // card text this session (e.g. "被ダメージ-7%", "追加ダメージの威力：240").
    private static readonly (string Label, string Keyword)[] TreasureCategories =
    [
        ("最大HP", "最大HP"),
        ("与ダメージUP", "与ダメージ"),
        ("被ダメージ減少", "被ダメージ"),
        ("追加ダメージ系", "追加ダメージ"),
    ];

    private string filter = string.Empty;
    private int selectedIndex;
    private int selectedSection;

    private static readonly (string Label, string Description)[] Sections =
    [
        ("魔獣捕獲", "捕獲済み・未捕獲の一覧、移動先の記録・ナビゲーション・図鑑同期"),
        ("闘獣練", "闘獣練(Crucible)の自動化"),
        ("魔獣リスト", "現在所持している魔獣のランク・経験値の閲覧専用テーブル"),
    ];

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
        CruciblePetPartyService cruciblePetParty,
        CrucibleAutoDutyIpcService crucibleAutoDuty,
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
        this.cruciblePetParty = cruciblePetParty;
        this.crucibleAutoDuty = crucibleAutoDuty;
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

    // A single teal-gold "beastmaster" accent theme, applied consistently to
    // the sidebar's selected item, section headers, and the beast list's
    // rank/level highlights — cheap (color pushes only, no extra fonts or
    // textures) but ties the whole window together instead of leaving it as
    // default-gray ImGui.
    private static readonly Vector4 AccentColor = new(0.36f, 0.78f, 0.64f, 1f);
    private static readonly Vector4 AccentColorDim = new(0.36f, 0.78f, 0.64f, 0.35f);
    private static readonly Vector4 GoldColor = new(0.95f, 0.75f, 0.28f, 1f);

    private static readonly Vector4[] LevelColors =
    [
        new Vector4(0.68f, 0.68f, 0.72f, 1f),
        new Vector4(0.55f, 0.85f, 0.58f, 1f),
        new Vector4(0.45f, 0.68f, 0.95f, 1f),
        new Vector4(0.78f, 0.52f, 0.95f, 1f),
        GoldColor,
    ];

    // Beast level tops out around 25 (per the user) — bucket it into 5 bands.
    private static Vector4 ColorForLevel(int level)
        => LevelColors[Math.Clamp((level - 1) / 5, 0, LevelColors.Length - 1)];

    private static int RankForDisplay(BeastPet pet, BeastProgress progress)
    {
        var rank = progress.Level;
        if (rank is >= 1 and <= 25)
        {
            return rank;
        }

        return pet.Rank;
    }

    public override void Draw()
    {
        using var frameRounding = PushStyle(ImGuiStyleVar.FrameRounding, 4f);
        using var grabRounding = PushStyle(ImGuiStyleVar.GrabRounding, 4f);

        if (ImGui.BeginChild("##BeastHelperSidebar", new Vector2(160, 0), true))
        {
            ImGui.SetWindowFontScale(1.1f);
            using (PushColor(ImGuiCol.Text, AccentColor))
            {
                ImGui.TextUnformatted("BeastHelper");
            }

            ImGui.SetWindowFontScale(1f);
            ImGui.TextDisabled(VersionLabel);
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            for (var i = 0; i < Sections.Length; i++)
            {
                var (label, description) = Sections[i];
                var selected = this.selectedSection == i;
                using var selectedColor = PushColor(ImGuiCol.HeaderActive, AccentColorDim, selected);
                using var selectedColorHover = PushColor(ImGuiCol.HeaderHovered, AccentColorDim, selected);
                using var selectedColorBg = PushColor(ImGuiCol.Header, AccentColorDim, selected);
                if (ImGui.Selectable($"{label}##section{i}", selected, ImGuiSelectableFlags.None, new Vector2(0, 28)))
                {
                    this.selectedSection = i;
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(description);
                }
            }
        }

        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild("##BeastHelperContent"))
        {
            var (label, _) = Sections[this.selectedSection];
            ImGui.SetWindowFontScale(1.15f);
            using (PushColor(ImGuiCol.Text, AccentColor))
            {
                ImGui.TextUnformatted(label);
            }

            ImGui.SetWindowFontScale(1f);
            ImGui.Separator();
            ImGui.Spacing();

            switch (this.selectedSection)
            {
                case 0:
                    this.DrawCaptureSection();
                    break;
                case 1:
                    this.DrawCrucibleTab();
                    break;
                case 2:
                    this.DrawOwnedBeastListSection();
                    break;
            }
        }

        ImGui.EndChild();
    }

    private static IDisposable PushStyle(ImGuiStyleVar var, float value)
    {
        ImGui.PushStyleVar(var, value);
        return new PopStyleVarOnDispose();
    }

    private static IDisposable PushColor(ImGuiCol col, Vector4 color, bool condition = true)
    {
        if (!condition)
        {
            return NullDisposable.Instance;
        }

        ImGui.PushStyleColor(col, color);
        return new PopStyleColorOnDispose();
    }

    private sealed class PopStyleVarOnDispose : IDisposable
    {
        public void Dispose() => ImGui.PopStyleVar();
    }

    private sealed class PopStyleColorOnDispose : IDisposable
    {
        public void Dispose() => ImGui.PopStyleColor();
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    private void DrawCaptureSection()
    {
        this.DrawControls();
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Filter by name, No, or description", ref this.filter, 256);
        ImGui.Separator();
        this.DrawPetList();
    }

    /// <summary>View-only table of currently-owned beasts and their rank/level/exp — no move/map actions, no filter.</summary>
    private void DrawOwnedBeastListSection()
    {
        var owned = this.beastData.Pets
            .Where(pet => this.configuration.TamedPetRowIds.Contains(pet.PetRowId))
            .OrderBy(pet => pet.XbmRowId)
            .ToArray();

        ImGui.TextDisabled($"所持数: {owned.Length}/{this.beastData.Pets.Count}");

        var autoSyncLevel = this.configuration.AutoSyncBeastLevelEnabled;
        if (ImGui.Checkbox("魔獣手帳を開いたらランク/経験値を自動同期", ref autoSyncLevel))
        {
            this.configuration.AutoSyncBeastLevelEnabled = autoSyncLevel;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "魔獣手帳(XBMMonsterNotebook)が開かれたら自動でページ送り・各行の\n"
                + "選択を行い、50体分のランク/経験値を手帳上で一度に読み込みます\n"
                + "(手動でホバーする必要がありません)。選択はゲーム内Agentへの\n"
                + "小さなイベント送信によるもので、生のUIクリック再現ではありません。\n"
                + "手帳を閉じると中断します。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled(this.tamingState.NotebookSyncStatus);

        ImGui.Spacing();

        if (owned.Length == 0)
        {
            ImGui.TextUnformatted("まだ捕獲した魔獣がいません。");
            return;
        }

        if (ImGui.BeginTable("##beasthelperOwnedPets", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("編成", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("No", ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableSetupColumn("名前", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("RANK", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("EXP", ImGuiTableColumnFlags.WidthFixed, 140);
            ImGui.TableHeadersRow();

            foreach (var pet in owned)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                var inManualTeam = this.configuration.CrucibleManualTeamPetRowIds.Contains(pet.PetRowId);
                if (ImGui.Checkbox($"##team{pet.PetRowId}", ref inManualTeam))
                {
                    if (inManualTeam)
                    {
                        this.configuration.CrucibleManualTeamPetRowIds.Add(pet.PetRowId);
                    }
                    else
                    {
                        this.configuration.CrucibleManualTeamPetRowIds.Remove(pet.PetRowId);
                    }

                    this.saveConfiguration();
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("闘獣練タブの編成モードが「手動」のとき、ここでチェックした魔獣で編成します。");
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(pet.XbmRowId.ToString());

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(pet.Name);

                ImGui.TableSetColumnIndex(3);
                this.configuration.BeastLevelProgress.TryGetValue(pet.PetRowId, out var progress);
                if (progress is null)
                {
                    ImGui.TextDisabled("-");
                    ImGui.TableSetColumnIndex(4);
                    ImGui.TextDisabled("-");
                    continue;
                }

                var rank = RankForDisplay(pet, progress);
                if (rank <= 0)
                {
                    ImGui.TextDisabled("-");
                }
                else
                {
                    using (PushColor(ImGuiCol.Text, ColorForLevel(rank)))
                    {
                        ImGui.TextUnformatted(rank.ToString());
                    }
                }

                ImGui.TableSetColumnIndex(4);
                if (progress.MaxExp <= 0)
                {
                    ImGui.TextDisabled("-");
                    continue;
                }

                var fraction = Math.Clamp(progress.Exp / (float)progress.MaxExp, 0f, 1f);
                using (PushColor(ImGuiCol.PlotHistogram, GoldColor))
                {
                    ImGui.ProgressBar(fraction, new Vector2(120, 16), $"{progress.Exp}/{progress.MaxExp}");
                }
            }

            ImGui.EndTable();
        }
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
    }

    private void DrawCrucibleTab()
    {
        ImGui.TextWrapped("闘獣練 (Crucible of the Unbroken) 向けの実験的機能です。");
        ImGui.TextWrapped("盤内のマスからマスへの移動は自動化できません。手動で移動してください。");
        ImGui.Spacing();

        // === 自動突入 =========================================================
        ImGui.TextUnformatted("自動突入");
        ImGui.Separator();

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
                + "参加確認(突入)、盤内の宝箱画面(下の「宝箱取得設定」参照)、\n"
                + "ショップ画面(何も買わずに終了)、戦闘後の結果画面の「次のページ」\n"
                + "/「退出」、戦利品画面の「すべて入手」とその確認ダイアログまで、\n"
                + "一連の流れを自動で進めます。\n"
                + "ルート・コンテンツ内容の選択には触れません(手動で行ってください)。\n"
                + "魔獣編成・呼び笛の割り当ては下の別セクションで分けています。\n"
                + "注意: 参加確認(ContentsFinderConfirm)はゲーム共通の画面なので、ONの間は\n"
                + "闘獣練以外のコンテンツの参加確認も自動で「突入」してしまいます。");
        }

        var autoDutyMode = this.configuration.CrucibleAutoDutyModeEnabled;
        using (PushColor(ImGuiCol.Text, GoldColor))
        {
            if (ImGui.Checkbox("盤面全体をAutoDutyに任せる(実験的)", ref autoDutyMode))
            {
                this.configuration.CrucibleAutoDutyModeEnabled = autoDutyMode;
                this.saveConfiguration();
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "ONにすると、以下の魔獣編成・呼び笛・宝箱・ショップ・アイテム・盤面選択\n"
                + "セクションのチェックは全て無視され、代わりにAutoDuty(要導入)へ\n"
                + "IPCで丸ごと引き継ぎます(AutoDuty.Run(territoryType, 周回回数))。\n"
                + "AutoDuty側で闘獣練のパス/設定を事前に用意しておく必要があります。\n"
                + "両方同時に有効だと同じ画面のボタンを取り合うため、この状態では\n"
                + "BeastHelper側のCrucible自動化は完全に停止します(図鑑/レベル同期\n"
                + "とBMR回避は引き続き動作します)。");
        }

        if (autoDutyMode)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(this.crucibleAutoDuty.Status);
            ImGui.TextDisabled("↑ONの間、下の魔獣編成/呼び笛/宝箱/ショップ/アイテム/盤面選択の設定は使われません。");
        }

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

        var loopCount = this.configuration.CrucibleLoopCount;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("周回回数(0=無制限)", ref loopCount, 1, 5))
        {
            this.configuration.CrucibleLoopCount = Math.Max(0, loopCount);
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "突入NPCを再度検知できたタイミングを「1周完了」の区切りとして数え、\n"
                + "指定した回数に達したら自動突入を自動でOFFに戻します。\n"
                + "0なら無制限に周回し続けます。1なら1回きりで周回しません。\n"
                + "カウントは自動突入を手動でONにするたびリセットされます。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled(this.crucibleLoop.Status);

        ImGui.Spacing();
        ImGui.Separator();

        // === 魔獣編成 / 呼び笛 =================================================
        ImGui.TextUnformatted("魔獣編成 / 呼び笛");
        ImGui.Separator();

        var autoPetParty = this.configuration.AutoCruciblePetPartyEnabled;
        if (ImGui.Checkbox("魔獣編成を自動化", ref autoPetParty))
        {
            this.configuration.AutoCruciblePetPartyEnabled = autoPetParty;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "呼び笛の設定画面(XBMPetParty)が開いている間、目標の編成と\n"
                + "食い違う魔獣を外し、足りない魔獣を魔獣手帳から追加します。\n"
                + "手帳のクリックはUIクリックの再現ではなく、ゲーム内Agentへの\n"
                + "FireCallbackとMouseDownイベント送信によるものです(実機で\n"
                + "確認済み)。");
        }

        var teamMode = this.configuration.CrucibleTeamMode;
        ImGui.SetNextItemWidth(180);
        if (ImGui.BeginCombo("編成モード", teamMode == CrucibleTeamMode.Leveling ? "レベリング(低ランク優先)" : "手動(魔獣リストで選択)"))
        {
            if (ImGui.Selectable("レベリング(低ランク優先)", teamMode == CrucibleTeamMode.Leveling))
            {
                this.configuration.CrucibleTeamMode = CrucibleTeamMode.Leveling;
                this.saveConfiguration();
            }

            if (ImGui.Selectable("手動(魔獣リストで選択)", teamMode == CrucibleTeamMode.Manual))
            {
                this.configuration.CrucibleTeamMode = CrucibleTeamMode.Manual;
                this.saveConfiguration();
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(this.cruciblePetParty.Status);

        if (teamMode == CrucibleTeamMode.Manual)
        {
            ImGui.TextDisabled("※手動モードの場合、先に「魔獣リスト」から編成する魔獣を選択してください。");
        }

        ImGui.Spacing();

        var autoWhistle = this.configuration.AutoCrucibleWhistleAssignEnabled;
        if (ImGui.Checkbox("呼び笛の割り当てと戦闘開始を自動化", ref autoWhistle))
        {
            this.configuration.AutoCrucibleWhistleAssignEnabled = autoWhistle;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "戦闘開始直前の画面(XBMStageDetailList)で、編成中の魔獣から\n"
                + "上の編成モードに応じて最大3体を選び、呼び笛1〜3に自動で\n"
                + "割り当てます(callback(\"XBMPetParty\", 1, row) — 除去で使って\n"
                + "いるのと同じ行インデックス方式)。割り当てが完了したら、続けて\n"
                + "「戦闘開始」ボタンも自動で押します(実機で確認済み)。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled(this.cruciblePetParty.WhistleStatus);

        ImGui.Spacing();
        ImGui.Separator();

        // === BMR / RSR =========================================================
        ImGui.TextUnformatted("BMR / RSR");
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
                + "いなくなったら自動でOFFに戻します。技・ローテーション・ポジショナルの\n"
                + "選択には一切触れません(すべてRSR側に任せます)。RSR未導入の場合は\n"
                + "戦闘の自動切替は何も起きません。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{this.crucibleCombat.Status} / RSR: {(this.crucibleCombat.RsrReportsActive ? "active" : "inactive")}");

        ImGui.Spacing();
        ImGui.Separator();

        // === 宝箱取得設定 / アイテム ===========================================
        ImGui.TextUnformatted("宝箱取得設定 / アイテム");
        ImGui.Separator();

        ImGui.TextUnformatted("宝箱の取得優先度 (優先度1が最優先)");
        var treasureSlots = new string?[TreasureCategories.Length];
        for (var i = 0; i < treasureSlots.Length; i++)
        {
            treasureSlots[i] = i < this.configuration.CrucibleTreasureKeywords.Count ? this.configuration.CrucibleTreasureKeywords[i] : null;
        }

        var treasureChanged = false;
        for (var slot = 0; slot < treasureSlots.Length; slot++)
        {
            var currentLabel = LabelForTreasureKeyword(treasureSlots[slot]);
            ImGui.SetNextItemWidth(180);
            if (ImGui.BeginCombo($"優先度{slot + 1}##treasurePriority{slot}", currentLabel))
            {
                if (ImGui.Selectable(NoTreasureCategoryLabel, currentLabel == NoTreasureCategoryLabel))
                {
                    treasureSlots[slot] = null;
                    treasureChanged = true;
                }

                foreach (var (label, keyword) in TreasureCategories)
                {
                    if (ImGui.Selectable(label, currentLabel == label))
                    {
                        treasureSlots[slot] = keyword;
                        treasureChanged = true;
                    }
                }

                ImGui.EndCombo();
            }
        }

        if (treasureChanged)
        {
            this.configuration.CrucibleTreasureKeywords = treasureSlots.Where(k => k is not null).Select(k => k!).ToList();
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "宝箱で表示される4枚のアイテムカードの効果テキストと、上から順に\n"
                + "照合します。最初に一致したカテゴリを持つカードを自動で取得し、\n"
                + "どれにも一致しなければ(または未設定なら)1枚目のカードを取得します\n"
                + "(何も取得せず終了、はしません)。");
        }

        ImGui.Spacing();

        var autoShopBuy = this.configuration.AutoCrucibleShopBuyEnabled;
        if (ImGui.Checkbox("ショップで自動購入", ref autoShopBuy))
        {
            this.configuration.AutoCrucibleShopBuyEnabled = autoShopBuy;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "OFFの場合は今まで通り何も買わずに終了します。ONの場合、優先順位\n"
                + "(回復薬 > 防具(未所持のみ) > えさ(1回の来店で最大1個))で\n"
                + "買えるものを買ってから終了します。宝箱の優先キーワードと違い、\n"
                + "ショップ画面には照合できるテキストが無いため、AutoDutyで実績の\n"
                + "あるアイテムIDのカテゴリ単位でのON/OFFのみ対応しています。");
        }

        if (autoShopBuy)
        {
            ImGui.Indent();

            var buyHealing = this.configuration.CrucibleShopBuyHealing;
            if (ImGui.Checkbox("回復薬を購入", ref buyHealing))
            {
                this.configuration.CrucibleShopBuyHealing = buyHealing;
                this.saveConfiguration();
            }

            ImGui.SameLine();
            var healingCap = this.configuration.CrucibleShopHealingCap;
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("所持上限##shopHealingCap", ref healingCap, 1, 5))
            {
                this.configuration.CrucibleShopHealingCap = Math.Clamp(healingCap, 0, 99);
                this.saveConfiguration();
            }

            var buyGear = this.configuration.CrucibleShopBuyGear;
            if (ImGui.Checkbox("防具を購入(未所持のみ)", ref buyGear))
            {
                this.configuration.CrucibleShopBuyGear = buyGear;
                this.saveConfiguration();
            }

            var buyFeed = this.configuration.CrucibleShopBuyFeed;
            if (ImGui.Checkbox("えさを購入(1回の来店で最大1個)", ref buyFeed))
            {
                this.configuration.CrucibleShopBuyFeed = buyFeed;
                this.saveConfiguration();
            }

            ImGui.Unindent();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(this.crucibleShop.Status);

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

        var autoSelfBuffItems = this.configuration.AutoSelfBuffItemsEnabled;
        if (ImGui.Checkbox("自己バフアイテムを自動使用(回避の書/反射の書/時の砂/魔獣剛力薬)", ref autoSelfBuffItems))
        {
            this.configuration.AutoSelfBuffItemsEnabled = autoSelfBuffItems;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "戦闘中、これらの自己バフ系アイテムを共有クールダウンが明け次第\n"
                + "即座に使用します。攻撃を受けるタイミングやダメージウィンドウに\n"
                + "合わせるような賢い判断はしていません(使えたら使うだけ)。\n"
                + "回復薬と共有クールダウン(約2秒)のため、HPが閾値を下回っている\n"
                + "間は回復薬が優先されます。");
        }

        var autoOffensiveItems = this.configuration.AutoOffensiveItemsEnabled;
        if (ImGui.Checkbox("攻撃アイテムを自動使用(吸血鬼の牙/各種の牙)", ref autoOffensiveItems))
        {
            this.configuration.AutoOffensiveItemsEnabled = autoOffensiveItems;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "戦闘中、現在の敵対ターゲットに対してこれらの攻撃アイテムを\n"
                + "共有クールダウンが明け次第即座に使用します。ターゲットの弱点\n"
                + "属性に合わせて牙の種類を選ぶような判定はしていません(固定の\n"
                + "優先順で試すだけ)。敵をターゲットしていない間は何もしません。");
        }

        ImGui.Spacing();

        var autoCrucibleItemRules = this.configuration.AutoCrucibleItemRulesEnabled;
        if (ImGui.Checkbox("アイテム使用ルールを有効化", ref autoCrucibleItemRules))
        {
            this.configuration.AutoCrucibleItemRulesEnabled = autoCrucibleItemRules;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "条件(自身/ターゲットのステータス有無、自身/ターゲットのHP閾値、\n"
                + "ターゲットのモンスターID)を満たしたときだけ指定したアイテムを\n"
                + "使用する、独自ルールを設定できます。リストの上から順に判定し、\n"
                + "最初に条件を満たした有効なルールのアイテムを使用します(使用\n"
                + "できなければ次のルールへ)。上の3つのチェックボックスより先に\n"
                + "評価されます。");
        }

        this.DrawCrucibleItemRules();
    }

    private static readonly CrucibleItemRuleConditionType[] ConditionTypeValues =
    [
        CrucibleItemRuleConditionType.SelfStatus,
        CrucibleItemRuleConditionType.TargetStatus,
        CrucibleItemRuleConditionType.SelfHp,
        CrucibleItemRuleConditionType.TargetHp,
        CrucibleItemRuleConditionType.TargetDataId,
    ];

    private static readonly string[] ConditionTypeLabels =
        ["自身ステータス", "ターゲットステータス", "自身HP", "ターゲットHP", "ターゲットのモンスターID"];

    private void DrawCrucibleItemRules()
    {
        var rules = this.configuration.CrucibleItemRules;
        ImGui.Indent();

        CrucibleItemRule? ruleToRemove = null;
        for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
        {
            var rule = rules[ruleIndex];
            ImGui.PushID(ruleIndex);

            var itemLabel = rule.ItemId == 0 ? "(未選択)" : CrucibleRecoveryItemService.GetItemName(rule.ItemId);
            var headerLabel = $"{(rule.Enabled ? string.Empty : "[無効] ")}{rule.Name} → {itemLabel}###ruleHeader";
            if (ImGui.CollapsingHeader(headerLabel))
            {
                var enabled = rule.Enabled;
                if (ImGui.Checkbox("有効##ruleEnabled", ref enabled))
                {
                    rule.Enabled = enabled;
                    this.saveConfiguration();
                }

                var name = rule.Name;
                ImGui.SetNextItemWidth(200);
                if (ImGui.InputText("ルール名##ruleName", ref name, 64))
                {
                    rule.Name = name;
                    this.saveConfiguration();
                }

                ImGui.SetNextItemWidth(200);
                if (ImGui.BeginCombo("使用アイテム##ruleItem", itemLabel))
                {
                    foreach (var candidateId in CrucibleRecoveryItemService.KnownItemIds)
                    {
                        var candidateLabel = CrucibleRecoveryItemService.GetItemName(candidateId);
                        if (ImGui.Selectable($"{candidateLabel}##item{candidateId}", rule.ItemId == candidateId))
                        {
                            rule.ItemId = candidateId;
                            this.saveConfiguration();
                        }
                    }

                    ImGui.EndCombo();
                }

                var joinAll = rule.ConditionJoinMode == CrucibleItemRuleConditionJoinMode.All;
                ImGui.SetNextItemWidth(160);
                if (ImGui.BeginCombo("条件の結合##ruleJoin", joinAll ? "すべて満たす(AND)" : "いずれか満たす(OR)"))
                {
                    if (ImGui.Selectable("すべて満たす(AND)", joinAll))
                    {
                        rule.ConditionJoinMode = CrucibleItemRuleConditionJoinMode.All;
                        this.saveConfiguration();
                    }

                    if (ImGui.Selectable("いずれか満たす(OR)", !joinAll))
                    {
                        rule.ConditionJoinMode = CrucibleItemRuleConditionJoinMode.Any;
                        this.saveConfiguration();
                    }

                    ImGui.EndCombo();
                }

                ImGui.TextUnformatted("条件:");
                CrucibleItemRuleCondition? conditionToRemove = null;
                for (var conditionIndex = 0; conditionIndex < rule.Conditions.Count; conditionIndex++)
                {
                    ImGui.PushID(conditionIndex);
                    this.DrawCrucibleItemRuleCondition(rule.Conditions[conditionIndex]);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("削除##conditionRemove"))
                    {
                        conditionToRemove = rule.Conditions[conditionIndex];
                    }

                    ImGui.PopID();
                }

                if (conditionToRemove is not null)
                {
                    rule.Conditions.Remove(conditionToRemove);
                    this.saveConfiguration();
                }

                if (ImGui.SmallButton("条件を追加##addCondition"))
                {
                    rule.Conditions.Add(new CrucibleItemRuleCondition());
                    this.saveConfiguration();
                }

                ImGui.Spacing();
                if (ImGui.SmallButton("このルールを削除##removeRule"))
                {
                    ruleToRemove = rule;
                }
            }

            ImGui.PopID();
        }

        if (ruleToRemove is not null)
        {
            rules.Remove(ruleToRemove);
            this.saveConfiguration();
        }

        if (ImGui.SmallButton("+ ルールを追加"))
        {
            rules.Add(new CrucibleItemRule());
            this.saveConfiguration();
        }

        ImGui.Unindent();
    }

    private void DrawCrucibleItemRuleCondition(CrucibleItemRuleCondition condition)
    {
        var typeIndex = Array.IndexOf(ConditionTypeValues, condition.Type);
        var typeLabel = typeIndex >= 0 ? ConditionTypeLabels[typeIndex] : ConditionTypeLabels[0];
        ImGui.SetNextItemWidth(170);
        if (ImGui.BeginCombo("種類##conditionType", typeLabel))
        {
            for (var i = 0; i < ConditionTypeValues.Length; i++)
            {
                if (ImGui.Selectable(ConditionTypeLabels[i], condition.Type == ConditionTypeValues[i]))
                {
                    condition.Type = ConditionTypeValues[i];
                    this.saveConfiguration();
                }
            }

            ImGui.EndCombo();
        }

        switch (condition.Type)
        {
            case CrucibleItemRuleConditionType.SelfStatus:
            case CrucibleItemRuleConditionType.TargetStatus:
            {
                ImGui.SameLine();
                var present = condition.StatusCondition == CrucibleItemRuleStatusCondition.Present;
                ImGui.SetNextItemWidth(90);
                if (ImGui.BeginCombo("状態##conditionStatusCond", present ? "付与時" : "未付与時"))
                {
                    if (ImGui.Selectable("付与時", present))
                    {
                        condition.StatusCondition = CrucibleItemRuleStatusCondition.Present;
                        this.saveConfiguration();
                    }

                    if (ImGui.Selectable("未付与時", !present))
                    {
                        condition.StatusCondition = CrucibleItemRuleStatusCondition.Missing;
                        this.saveConfiguration();
                    }

                    ImGui.EndCombo();
                }

                ImGui.SameLine();
                var statusId = (int)condition.StatusId;
                ImGui.SetNextItemWidth(90);
                if (ImGui.InputInt("ステータスID##conditionStatusId", ref statusId))
                {
                    condition.StatusId = (uint)Math.Max(0, statusId);
                    this.saveConfiguration();
                }

                break;
            }

            case CrucibleItemRuleConditionType.SelfHp:
            case CrucibleItemRuleConditionType.TargetHp:
            {
                ImGui.SameLine();
                var above = condition.HpCondition == CrucibleItemRuleHpCondition.Above;
                ImGui.SetNextItemWidth(80);
                if (ImGui.BeginCombo("##conditionHpCond", above ? "以上" : "未満"))
                {
                    if (ImGui.Selectable("以上", above))
                    {
                        condition.HpCondition = CrucibleItemRuleHpCondition.Above;
                        this.saveConfiguration();
                    }

                    if (ImGui.Selectable("未満", !above))
                    {
                        condition.HpCondition = CrucibleItemRuleHpCondition.Below;
                        this.saveConfiguration();
                    }

                    ImGui.EndCombo();
                }

                ImGui.SameLine();
                var threshold = condition.HpThreshold;
                ImGui.SetNextItemWidth(110);
                if (ImGui.SliderFloat("HP%##conditionHpThreshold", ref threshold, 1f, 100f, "%.0f"))
                {
                    condition.HpThreshold = Math.Clamp(threshold, 1f, 100f);
                    this.saveConfiguration();
                }

                break;
            }

            case CrucibleItemRuleConditionType.TargetDataId:
            {
                ImGui.SameLine();
                var dataId = (int)condition.DataId;
                ImGui.SetNextItemWidth(120);
                if (ImGui.InputInt("モンスターID##conditionDataId", ref dataId))
                {
                    condition.DataId = (uint)Math.Max(0, dataId);
                    this.saveConfiguration();
                }

                break;
            }
        }
    }

    private static string LabelForTreasureKeyword(string? keyword)
    {
        if (keyword is null)
        {
            return NoTreasureCategoryLabel;
        }

        foreach (var (label, candidateKeyword) in TreasureCategories)
        {
            if (candidateKeyword == keyword)
            {
                return label;
            }
        }

        return NoTreasureCategoryLabel;
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

        if (ImGui.BeginTable("##beasthelperPets", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("No", ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.9f);
            ImGui.TableSetupColumn("RANK", ImGuiTableColumnFlags.WidthFixed, 90);
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
                if (captured)
                {
                    using (PushColor(ImGuiCol.Text, AccentColor))
                    {
                        ImGui.TextUnformatted("[済]");
                    }

                    ImGui.SameLine();
                }

                if (ImGui.Selectable($"{pet.Name}##{pet.PetRowId}", i == this.selectedIndex))
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
                this.configuration.BeastLevelProgress.TryGetValue(pet.PetRowId, out var progress);
                if (progress is null)
                {
                    ImGui.TextDisabled("-");
                }
                else
                {
                    var rank = RankForDisplay(pet, progress);
                    if (rank <= 0)
                    {
                        ImGui.TextDisabled("-");
                    }
                    else
                    {
                        using (PushColor(ImGuiCol.Text, ColorForLevel(rank)))
                        {
                            ImGui.TextUnformatted(rank.ToString());
                        }
                    }

                    if (progress.MaxExp > 0)
                    {
                        var fraction = Math.Clamp(progress.Exp / (float)progress.MaxExp, 0f, 1f);
                        using (PushColor(ImGuiCol.PlotHistogram, GoldColor))
                        {
                            ImGui.ProgressBar(fraction, new Vector2(56, 6), string.Empty);
                        }

                        if (ImGui.IsItemHovered())
                        {
                            ImGui.SetTooltip($"EXP {progress.Exp}/{progress.MaxExp}\n(魔獣手帳の詳細を開いたときに読み取った値です)");
                        }
                    }
                }

                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(destination is null ? "-" : DestinationLabel(destination));

                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted($"{pet.Stat1}/{pet.Stat2}/{pet.Stat3}/{pet.Stat4}/{pet.Stat5}");

                ImGui.TableSetColumnIndex(5);
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
