using System.Numerics;
using BeastNav.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BeastNav.Windows;

public sealed class DebugWindow : Window
{
    private readonly BeastTamingStateService tamingState;
    private readonly LuaAutomationService luaAutomation;
    private readonly LuaRecorderService luaRecorder;
    private readonly NearbyObjectDumpService nearbyObjectDump;
    private string luaTestInput = string.Empty;

    public DebugWindow(
        BeastTamingStateService tamingState,
        LuaAutomationService luaAutomation,
        LuaRecorderService luaRecorder,
        NearbyObjectDumpService nearbyObjectDump)
        : base("BeastHelper Debug###BeastHelperDebug")
    {
        this.tamingState = tamingState;
        this.luaAutomation = luaAutomation;
        this.luaRecorder = luaRecorder;
        this.nearbyObjectDump = nearbyObjectDump;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 280),
            MaximumSize = new Vector2(1400, 900),
        };
        this.RespectCloseHotkey = true;

        // A stale saved ImGui position (e.g. from a different monitor layout)
        // can otherwise put this off-screen with no visible sign it opened at
        // all. Force a known on-screen spot every time — this is a debug
        // window, not something worth remembering a moved position for.
        this.Position = new Vector2(120, 120);
        this.PositionCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        if (ImGui.Button("Sync 図鑑 now"))
        {
            this.tamingState.TrySync();
        }

        ImGui.SameLine();
        if (ImGui.Button("Dump note addons to log"))
        {
            this.tamingState.DumpNoteAddons();
        }

        ImGui.SameLine();
        if (ImGui.Button("Dump nearby objects to log"))
        {
            this.nearbyObjectDump.DumpNearbyObjects();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("周囲100y以内の全GameObject(種別・BaseId・名前・座標・距離・ターゲット可否)を/xllogに出力します。");
        }

        ImGui.Separator();

        var sync = this.tamingState.LastResult;
        ImGui.TextWrapped($"Last 図鑑 sync: {sync.Message}");
        ImGui.TextUnformatted($"Strategy: {sync.Strategy}  Source: {sync.Source}");
        ImGui.TextWrapped($"Method: {sync.Method}");
        ImGui.TextUnformatted($"Detected captured: {sync.TamedPetRowIds.Count}  Success: {sync.Success}  Applied: {sync.Synced}");

        if (sync.DataSize != 0 || sync.FileSize != 0)
        {
            ImGui.TextUnformatted($"module: data={sync.DataSize} file={sync.FileSize} header={sync.HeaderLength} type='{sync.FileType}' ver={sync.FileVersion}");
        }

        if (!string.IsNullOrWhiteSpace(sync.PayloadHex))
        {
            ImGui.TextWrapped($"Payload: {sync.PayloadHex}");
        }

        if (sync.Diagnostics.Count > 0 && ImGui.CollapsingHeader($"Diagnostics ({sync.Diagnostics.Count})"))
        {
            foreach (var line in sync.Diagnostics)
            {
                ImGui.TextUnformatted(line);
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Lua test (BeastHelper-internal MoonSharp)");
        ImGui.SetNextItemWidth(-90);
        ImGui.InputText("##luatest", ref this.luaTestInput, 512);
        ImGui.SameLine();
        if (ImGui.Button("実行##luatest"))
        {
            this.luaAutomation.Run(this.luaTestInput);
        }

        ImGui.TextDisabled($"e.g. click(\"AddonName\", 4) / selectItem(\"AddonName\", 11, 0) / callback(\"AddonName\", 1, 2)  —  {this.luaAutomation.Status}");

        ImGui.Separator();
        var recording = this.luaRecorder.IsRecording;
        if (ImGui.Checkbox("操作をログに記録", ref recording))
        {
            this.luaRecorder.SetEnabled(recording);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "ONの間、ゲーム側で実際に発生した操作(FireCallback、例:選択肢の決定や\n"
                + "確認ダイアログなど)を、そのままLuaの callback(\"アドオン名\", 値...) 行として\n"
                + "/xllogに記録します。手動で1回操作するだけで、そのLua行をログから\n"
                + "コピーしてスクリプトに貼り付けられます。\n"
                + "戦闘開始・すべて入手のような単純ボタンクリックはFireCallbackを使わないため\n"
                + "記録対象外です(それらは専用の自動化がすでにあります)。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled(this.luaRecorder.Status);
    }
}
