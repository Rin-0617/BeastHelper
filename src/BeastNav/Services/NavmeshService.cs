using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace BeastNav.Services;

/// <summary>
/// Thin wrapper around the vnavmesh IPC. Owns a single active navigation
/// request and nurses it along frame by frame: it waits for the navmesh to be
/// ready, snaps the (height-less) destination onto the mesh, re-issues the move
/// when vnavmesh fails to produce a path, downgrades flight to walking when the
/// player is not actually on a mount, and stops once the player arrives.
/// </summary>
public sealed class NavmeshService
{
    private const int MaxAttempts = 5;
    private const int FlyAttempts = 2;
    private const float PreciseSnapDistance = 6f;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SnapRefineInterval = TimeSpan.FromSeconds(2);

    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<bool> navReady;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPoint;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<Vector3, bool, bool> moveTo;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<bool> pathRunning;
    private readonly ICallGateSubscriber<int> pathWaypoints;
    private readonly ICallGateSubscriber<object?> pathStop;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object?> pathMoveTo;

    private Request? active;

    public NavmeshService(IDalamudPluginInterface pluginInterface, ICondition condition, IPluginLog log)
    {
        this.condition = condition;
        this.log = log;
        this.navReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        this.nearestPoint = pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
        this.pointOnFloor = pluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        this.moveCloseTo = pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        this.moveTo = pluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        this.pathfindInProgress = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        this.pathRunning = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        this.pathWaypoints = pluginInterface.GetIpcSubscriber<int>("vnavmesh.Path.NumWaypoints");
        this.pathStop = pluginInterface.GetIpcSubscriber<object?>("vnavmesh.Path.Stop");
        this.pathMoveTo = pluginInterface.GetIpcSubscriber<List<Vector3>, bool, object?>("vnavmesh.Path.MoveTo");
    }

    public bool IsReady() => Safe(this.navReady, false);

    /// <summary>True while a BeastHelper travel move is in progress.</summary>
    public bool HasActiveRequest => this.active is not null;

    /// <summary>
    /// Walk straight to a single point, no pathfinding — for short reactive
    /// moves like a dodge. Does not touch the travel <see cref="Request"/> state.
    /// </summary>
    public void WalkDirectlyTo(Vector3 point)
    {
        try
        {
            this.pathMoveTo.InvokeAction([point], false);
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] vnavmesh Path.MoveTo failed.");
        }
    }

    /// <summary>Stop vnavmesh following whatever path it is on.</summary>
    public void StopPath()
    {
        try
        {
            this.pathStop.InvokeAction();
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] vnavmesh Path.Stop failed.");
        }
    }

    /// <summary>Queue a navigation request, replacing any previous one.</summary>
    public bool MoveCloseTo(Vector3 destination, bool fly, float range)
    {
        var now = DateTime.UtcNow;
        this.active = new Request
        {
            Destination = destination,
            Fly = fly,
            Range = MathF.Max(1f, range),
            Monitored = destination,
            NextAttempt = now,
            Deadline = now + OverallTimeout,
        };

        this.log.Information("[BeastHelper] Navigation queued to {Dest} (fly={Fly}, range={Range}).", destination, fly, range);
        return true;
    }

    public void Stop()
    {
        this.active = null;
        try
        {
            this.pathStop.InvokeAction();
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "vnavmesh stop request failed.");
        }
    }

    /// <summary>Drive the active request. Call once per framework tick.</summary>
    public void Update(Vector3 playerPosition)
    {
        if (this.active is not { } request)
        {
            return;
        }

        var now = DateTime.UtcNow;

        // Arrived? Horizontal distance only: flying destinations sit above the floor.
        var delta = new Vector2(playerPosition.X - request.Monitored.X, playerPosition.Z - request.Monitored.Z);
        if (request.Moving && delta.LengthSquared() <= request.Range * request.Range)
        {
            this.log.Information("[BeastHelper] Reached destination; stopping vnavmesh.");
            this.Stop();
            return;
        }

        if (now >= request.Deadline)
        {
            this.log.Warning("[BeastHelper] Navigation timed out after {Attempts} attempt(s); giving up.", request.Attempts);
            this.active = null;
            return;
        }

        if (now < request.NextAttempt)
        {
            return;
        }

        if (!this.IsReady())
        {
            // Navmesh for the current zone is still loading/building.
            request.NextAttempt = now + ReadyPollInterval;
            return;
        }

        // Snap the destination onto the mesh once. Built-in destinations carry no
        // height (Y = 0), so vnavmesh flies/walks toward a point that is metres
        // above or below the real floor and never gets within arrival range.
        if (request.Resolved is null)
        {
            var snapped = this.SnapToMesh(request.Destination, playerPosition, announce: true);
            request.Resolved = snapped ?? request.Destination;
            request.Monitored = request.Resolved.Value;
            request.SnapRefined = snapped is { } first && IsPreciseSnap(first, request.Destination);
            request.NextSnapCheck = now + SnapRefineInterval;
        }

        // Already following a path — just keep monitoring for arrival.
        if (Safe(this.pathRunning, false) || Safe(this.pathWaypoints, 0) > 0)
        {
            if (!request.Moving)
            {
                this.log.Information("[BeastHelper] vnavmesh is following a path ({Count} waypoints).", Safe(this.pathWaypoints, 0));
            }

            request.Moving = true;
            this.TryRefineSnap(request, playerPosition, now);
            request.NextAttempt = now + RetryInterval;
            return;
        }

        // A pathfind is still running — let it finish before deciding anything.
        if (Safe(this.pathfindInProgress, false))
        {
            request.NextAttempt = now + ReadyPollInterval;
            return;
        }

        // We were moving and the path is gone now: treat it as complete.
        if (request.Moving)
        {
            this.log.Information("[BeastHelper] vnavmesh path finished.");
            this.active = null;
            return;
        }

        if (request.Attempts >= MaxAttempts)
        {
            this.log.Warning("[BeastHelper] vnavmesh produced no path after {Attempts} attempts; giving up.", request.Attempts);
            this.active = null;
            return;
        }

        this.Issue(request);
        request.Attempts++;
        request.NextAttempt = now + RetryInterval;
    }

    private void Issue(Request request)
    {
        var target = request.Resolved ?? request.Destination;

        // vnavmesh can only fly the character while it is actually on a mount.
        // If flight was requested but we're on foot — or the first couple of
        // flying attempts produced no path (e.g. flight not unlocked in this
        // zone) — fall back to a walking path so the player still gets there.
        var fly = request.Fly && this.Mounted() && request.Attempts < FlyAttempts;

        this.log.Information(
            "[BeastHelper] Requesting move to {Target} (fly={Fly}, attempt {Attempt}/{Max}).",
            target,
            fly,
            request.Attempts + 1,
            MaxAttempts);

        try
        {
            if (this.moveCloseTo.InvokeFunc(target, fly, request.Range))
            {
                return;
            }

            this.log.Debug("[BeastHelper] PathfindAndMoveCloseTo was rejected; trying PathfindAndMoveTo.");
            this.moveTo.InvokeFunc(target, fly);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[BeastHelper] vnavmesh move request failed.");
        }
    }

    private Vector3? SnapToMesh(Vector3 destination, Vector3 player, bool announce)
    {
        // The stored Y is unreliable (built-in destinations use 0), so probe
        // from the player's height — that is real ground level somewhere in this
        // same zone — and fall back to ever-wider searches around the raw point.
        var atPlayerHeight = new Vector3(destination.X, player.Y, destination.Z);

        var snapped =
            this.Query("NearestPoint@playerY", () => this.nearestPoint.InvokeFunc(atPlayerHeight, 30f, 300f))
            ?? this.Query("PointOnFloor@playerY", () => this.pointOnFloor.InvokeFunc(new Vector3(destination.X, player.Y + 100f, destination.Z), true, 30f))
            ?? this.Query("NearestPoint@raw", () => this.nearestPoint.InvokeFunc(destination, 30f, 1000f));

        if (!announce)
        {
            return snapped;
        }

        if (snapped is { } point)
        {
            this.log.Information("[BeastHelper] Snapped destination {Raw} -> {Snapped}.", destination, point);
        }
        else
        {
            this.log.Warning(
                "[BeastHelper] Could not snap {Raw} onto the navmesh (is vnavmesh's Query.Mesh IPC available?); using raw coordinates.",
                destination);
        }

        return snapped;
    }

    /// <summary>
    /// Right after a teleport the local mesh tile is often still streaming in even
    /// though <c>Nav.IsReady</c> is true, so the first snap can land tens of yalms
    /// off. Once we are moving and closer, re-snap and re-path if a materially
    /// better point is now available.
    /// </summary>
    private void TryRefineSnap(Request request, Vector3 player, DateTime now)
    {
        if (request.SnapRefined || now < request.NextSnapCheck || request.Resolved is not { } current)
        {
            return;
        }

        request.NextSnapCheck = now + SnapRefineInterval;

        var snapped = this.SnapToMesh(request.Destination, player, announce: false);
        if (snapped is not { } point || !IsPreciseSnap(point, request.Destination))
        {
            return;
        }

        request.SnapRefined = true;
        if (Vector3.DistanceSquared(point, current) > 4f * 4f)
        {
            this.log.Information("[BeastHelper] Refined destination {Old} -> {New}; re-pathing.", current, point);
            request.Resolved = point;
            request.Monitored = point;
            this.Issue(request);
        }
    }

    private static bool IsPreciseSnap(Vector3 snapped, Vector3 destination)
    {
        var dx = snapped.X - destination.X;
        var dz = snapped.Z - destination.Z;
        return (dx * dx) + (dz * dz) <= PreciseSnapDistance * PreciseSnapDistance;
    }

    private Vector3? Query(string label, Func<Vector3?> query)
    {
        try
        {
            var result = query();
            if (result is null)
            {
                this.log.Debug("[BeastHelper] Mesh query {Label} returned nothing.", label);
            }

            return result;
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Mesh query {Label} threw.", label);
            return null;
        }
    }

    private bool Mounted()
        => this.condition[ConditionFlag.Mounted] || this.condition[ConditionFlag.RidingPillion];

    private static T Safe<T>(ICallGateSubscriber<T> gate, T fallback)
    {
        try
        {
            return gate.InvokeFunc();
        }
        catch
        {
            return fallback;
        }
    }

    private sealed class Request
    {
        public Vector3 Destination;
        public bool Fly;
        public float Range;
        public Vector3? Resolved;
        public Vector3 Monitored;
        public int Attempts;
        public bool Moving;
        public bool SnapRefined;
        public DateTime NextAttempt;
        public DateTime NextSnapCheck;
        public DateTime Deadline;
    }
}
