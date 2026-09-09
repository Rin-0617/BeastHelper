using System.Numerics;
using BeastNav.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BeastNav.Windows;

public sealed class DebugWindow : Window
{
    private readonly BeastTamingStateService tamingState;

    public DebugWindow(BeastTamingStateService tamingState)
        : base("BeastHelper Debug###BeastHelperDebug")
    {
        this.tamingState = tamingState;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 280),
            MaximumSize = new Vector2(1400, 900),
        };
        this.RespectCloseHotkey = true;
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
    }
}
