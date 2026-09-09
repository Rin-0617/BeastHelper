using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace BeastNav.Services;

public sealed class NavmeshService
{
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<bool> navReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<Vector3, bool, bool> moveTo;
    private readonly ICallGateSubscriber<object?> stop;
    private Vector3? monitoredDestination;
    private float monitoredRange;

    public NavmeshService(IDalamudPluginInterface pluginInterface, IPluginLog log, IChatGui chat)
    {
        this.log = log;
        this.navReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        this.moveCloseTo = pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        this.moveTo = pluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        this.stop = pluginInterface.GetIpcSubscriber<object?>("vnavmesh.Path.Stop");
    }

    public bool IsReady()
    {
        try
        {
            return this.navReady.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public bool MoveCloseTo(Vector3 destination, bool fly, float range)
    {
        try
        {
            if (!this.IsReady())
            {
                this.log.Debug("vnavmesh readiness check failed; trying movement anyway.");
            }

            if (this.moveCloseTo.InvokeFunc(destination, fly, range))
            {
                this.monitoredDestination = destination;
                this.monitoredRange = MathF.Max(1f, range);
                return true;
            }

            this.log.Debug("vnavmesh close movement was rejected; trying normal movement.");
            var moved = this.moveTo.InvokeFunc(destination, fly);
            if (moved)
            {
                this.monitoredDestination = destination;
                this.monitoredRange = MathF.Max(1f, range);
            }

            return moved;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "vnavmesh move request failed.");
            return false;
        }
    }

    public void Stop()
    {
        this.monitoredDestination = null;
        try
        {
            this.stop.InvokeAction();
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "vnavmesh stop request failed.");
        }
    }

    public void CheckArrival(Vector3 playerPosition)
    {
        if (this.monitoredDestination is not { } destination)
        {
            return;
        }

        var delta = new Vector2(playerPosition.X - destination.X, playerPosition.Z - destination.Z);
        if (delta.LengthSquared() <= this.monitoredRange * this.monitoredRange)
        {
            this.log.Debug("Reached BeastHelper destination; stopping vnavmesh.");
            this.Stop();
        }
    }
}
