using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BeastNav.Services;

/// <summary>
/// Auto-clicks "突入" (commence) on the duty-ready confirmation
/// (<c>ContentsFinderConfirm</c>) that pops up once matching finishes,
/// letting the Crucible auto-entry loop continue past it without a manual
/// click. FFXIVClientStructs exposes this addon as a strongly-typed struct
/// with a <c>CommenceButton</c> field directly, so no node-id hunting is
/// needed — the same technique the community's AutoFrontline plugin uses for
/// this exact button.
/// </summary>
/// <remarks>
/// ContentsFinderConfirm is the game's shared ready-check addon for every
/// queued duty, not just Crucible. So the button is only clicked when the
/// popped duty (<c>ContentsFinder.QueueInfo.PoppedQueueEntry</c>) is one of
/// the Crucible boards (ContentFinderCondition 1088–1092). Any other duty,
/// a roulette, or an unreadable entry is left for the player to accept.
/// </remarks>
/// <remarks>
/// Unattended UI interaction — against the FFXIV ToS, self-responsibility.
/// Only ever touches the Commence button; never Withdraw or Wait.
/// </remarks>
public sealed unsafe class CrucibleReadyCheckService
{
    private const string ConfirmAddonName = "ContentsFinderConfirm";

    // 闘獣練 第一盤〜特二盤 (ContentFinderCondition rows).
    private const uint CrucibleConditionMin = 1088;
    private const uint CrucibleConditionMax = 1092;
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(1.5);

    private readonly Configuration configuration;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private DateTime nextAttempt = DateTime.MinValue;

    public CrucibleReadyCheckService(Configuration configuration, IGameGui gameGui, IPluginLog log)
    {
        this.configuration = configuration;
        this.gameGui = gameGui;
        this.log = log;
    }

    /// <summary>Short human status for the main window.</summary>
    public string Status { get; private set; } = "off";

    public void Tick()
    {
        if (!this.configuration.AutoEnterCrucibleEnabled)
        {
            this.Status = "off";
            return;
        }

        var now = DateTime.UtcNow;
        if (now < this.nextAttempt)
        {
            return;
        }

        AtkUnitBase* addon;
        try
        {
            var ptr = this.gameGui.GetAddonByName(ConfirmAddonName);
            addon = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible ready check: addon lookup failed.");
            return;
        }

        if (addon is null || !addon->IsVisible)
        {
            this.Status = "待機";
            return;
        }

        if (!IsCruciblePopped())
        {
            this.Status = "闘獣練以外の突入確認（押しません）";
            return;
        }

        var confirm = (AddonContentsFinderConfirm*)addon;
        var button = confirm->CommenceButton;
        if (button is null)
        {
            this.Status = "突入ボタン未検出";
            return;
        }

        var ownerNode = button->AtkComponentBase.OwnerNode;
        var evt = ownerNode is not null ? ownerNode->AtkResNode.AtkEventManager.Event : null;
        if (evt is null)
        {
            this.Status = "突入ボタン未検出";
            return;
        }

        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        this.nextAttempt = now + Throttle;
        this.Status = "突入をクリック";
        this.log.Information("[BeastHelper] Crucible ready check: clicked 突入.");
    }

    /// <summary>The ready check is for a Crucible board. Anything else (or unknown) returns false.</summary>
    private static bool IsCruciblePopped()
    {
        var finder = ContentsFinder.Instance();
        if (finder is null)
        {
            return false;
        }

        var popped = finder->QueueInfo.PoppedQueueEntry;
        return popped.ContentType == ContentsType.Regular
            && popped.Id is >= CrucibleConditionMin and <= CrucibleConditionMax;
    }
}
