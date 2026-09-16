using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BeastNav.Services;

/// <summary>
/// Hooks the game's own <c>AtkUnitBase.FireCallback</c> (the same native
/// function <see cref="LuaAutomationService"/>'s <c>callback(...)</c> and a
/// macro's "/callback" both call into) and logs every callback the game
/// fires — from the player's own manual clicks, not just BeastHelper's —
/// as a ready-to-paste Lua <c>callback("AddonName", ...)</c> line. Lets a
/// board's actual click/select flow be captured just by doing it once by
/// hand, instead of manually digging through Addon Inspector for node ids.
/// </summary>
/// <remarks>
/// Only covers addons that finish an action via FireCallback (SelectString,
/// SelectYesno, and many custom "confirm" screens do). A plain button whose
/// click never calls FireCallback — like XBMStageDetailList's 戦闘開始 or
/// XBMContentsBooty's すべて入手, both handled by their own dedicated
/// services already — won't show up here; those still need Addon Inspector.
/// </remarks>
public sealed unsafe class LuaRecorderService : IDisposable
{
    private readonly IGameInteropProvider hooks;
    private readonly IPluginLog log;
    private Hook<AtkUnitBase.Delegates.FireCallback>? hook;

    public LuaRecorderService(IGameInteropProvider hooks, IPluginLog log)
    {
        this.hooks = hooks;
        this.log = log;
    }

    public bool IsRecording => this.hook is { IsEnabled: true };

    /// <summary>Short human status for the main window.</summary>
    public string Status => this.IsRecording ? "記録中" : "停止中";

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            this.Enable();
        }
        else
        {
            this.Disable();
        }
    }

    private void Enable()
    {
        if (this.hook is not null)
        {
            return;
        }

        try
        {
            this.hook = this.hooks.HookFromAddress<AtkUnitBase.Delegates.FireCallback>(
                AtkUnitBase.MemberFunctionPointers.FireCallback,
                this.Detour);
            this.hook.Enable();
            this.log.Information("[BeastHelper] Lua recorder: enabled.");
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[BeastHelper] Lua recorder: failed to install hook.");
            this.hook = null;
        }
    }

    private void Disable()
    {
        if (this.hook is null)
        {
            return;
        }

        this.hook.Disable();
        this.hook.Dispose();
        this.hook = null;
        this.log.Information("[BeastHelper] Lua recorder: disabled.");
    }

    private bool Detour(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool updateState)
    {
        var result = this.hook!.Original(addon, valueCount, values, updateState);

        try
        {
            var addonName = addon->NameString;
            var args = new StringBuilder();
            for (var i = 0; i < valueCount; i++)
            {
                if (i > 0)
                {
                    args.Append(", ");
                }

                args.Append(ToLuaLiteral(values[i]));
            }

            this.log.Information("[BeastHelper] Lua recorder: callback(\"{Addon}\", {Args})", addonName, args.ToString());
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Lua recorder: failed to decode a FireCallback.");
        }

        return result;
    }

    private static string ToLuaLiteral(AtkValue value) => value.Type switch
    {
        AtkValueType.Int => value.Int.ToString(),
        AtkValueType.UInt => value.UInt.ToString(),
        AtkValueType.Bool => value.Bool ? "true" : "false",
        AtkValueType.Float => value.Float.ToString("0.###"),
        AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString =>
            $"\"{value.String.ToString().Replace("\"", "\\\"")}\"",
        _ => $"--[[{value.Type}]]",
    };

    public void Dispose() => this.Disable();
}
