using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace BeastNav.Services;

/// <summary>
/// Thin wrapper over WrathCombo's auto-rotation IPC. When WrathCombo is present
/// the Crucible combat assist hands the rotation to it (optimal skills for the
/// current job) instead of spamming action bar 1.
/// </summary>
public sealed class WrathComboBridge
{
    private const string InternalName = "BeastHelperCrucible";
    private const string DisplayName = "BeastHelper – Crucible";

    private readonly ICallGateSubscriber<string, string, Guid?> register;
    private readonly ICallGateSubscriber<Guid, object> setJobReady;
    private readonly ICallGateSubscriber<Guid, bool, object> setAutoState;
    private readonly ICallGateSubscriber<Guid, object?> release;
    private readonly IPluginLog log;

    private Guid? lease;
    private bool unavailable;

    public WrathComboBridge(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        this.register = pluginInterface.GetIpcSubscriber<string, string, Guid?>("WrathCombo.RegisterForLease");
        this.setJobReady = pluginInterface.GetIpcSubscriber<Guid, object>("WrathCombo.SetCurrentJobAutoRotationReady");
        this.setAutoState = pluginInterface.GetIpcSubscriber<Guid, bool, object>("WrathCombo.SetAutoRotationState");
        this.release = pluginInterface.GetIpcSubscriber<Guid, object?>("WrathCombo.ReleaseControl");
    }

    /// <summary>Whether a lease is (or can be) held — i.e. WrathCombo is usable.</summary>
    public bool Available => !this.unavailable && this.EnsureLease();

    public void SetAutoRotation(bool enabled)
    {
        if (!this.EnsureLease())
        {
            return;
        }

        try
        {
            if (enabled)
            {
                this.setJobReady.InvokeFunc(this.lease!.Value);
            }

            this.setAutoState.InvokeFunc(this.lease!.Value, enabled);
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] WrathCombo SetAutoRotationState failed; dropping lease.");
            this.lease = null;
        }
    }

    public void Release()
    {
        if (this.lease is not { } held)
        {
            return;
        }

        try
        {
            this.release.InvokeAction(held);
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] WrathCombo ReleaseControl failed.");
        }

        this.lease = null;
    }

    private bool EnsureLease()
    {
        if (this.lease is not null)
        {
            return true;
        }

        if (this.unavailable)
        {
            return false;
        }

        try
        {
            this.lease = this.register.InvokeFunc(InternalName, DisplayName);
            if (this.lease is null)
            {
                this.unavailable = true;
                return false;
            }

            this.log.Information("[BeastHelper] WrathCombo lease acquired for Crucible auto-combat.");
            return true;
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] WrathCombo IPC not available.");
            this.unavailable = true;
            return false;
        }
    }
}
