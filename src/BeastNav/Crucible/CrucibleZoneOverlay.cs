using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace BeastNav.Crucible;

/// <summary>
/// Draws the danger shapes and the computed dodge direction directly on the game
/// screen (world space, via <see cref="IGameGui.WorldToScreen"/>). This is the
/// dry-run for the dodge assist: it shows where the solver would send you, it
/// does not move you.
/// </summary>
public sealed unsafe class CrucibleZoneOverlay
{
    private const int CircleSegments = 40;
    private const int ConeSegments = 24;

    private static readonly uint DangerFill = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.15f, 0.15f, 0.22f));
    private static readonly uint DangerEdge = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.3f, 0.3f, 0.9f));
    private static readonly uint SafeArrow = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 1f, 0.9f, 1f));
    private static readonly uint NoSafe = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.8f, 0.2f, 1f));

    private readonly Configuration configuration;
    private readonly CrucibleStateReader reader;
    private readonly CrucibleDodgeSolver solver;
    private readonly IGameGui gameGui;

    public CrucibleZoneOverlay(
        Configuration configuration,
        CrucibleStateReader reader,
        CrucibleDodgeSolver solver,
        IGameGui gameGui)
    {
        this.configuration = configuration;
        this.reader = reader;
        this.solver = solver;
        this.gameGui = gameGui;
    }

    public void Draw()
    {
        if (!this.configuration.CrucibleOverlayEnabled || !this.configuration.CrucibleZonePaint)
        {
            return;
        }

        var state = this.reader.Current;
        if (!state.InCrucible || !state.HasPlayer)
        {
            return;
        }

        var plan = this.solver.Solve(state);
        var shapes = this.solver.LastShapes;
        if (shapes.Count == 0)
        {
            return;
        }

        var groundY = state.PlayerPosition.Y;
        var draw = ImGui.GetForegroundDrawList();

        foreach (var shape in shapes)
        {
            this.DrawShape(draw, shape, groundY);
        }

        this.DrawDodge(draw, state, plan, groundY);
    }

    private void DrawShape(ImDrawListPtr draw, DangerShape shape, float groundY)
    {
        var world = shape.Kind switch
        {
            DangerKind.Circle => CirclePoints(shape.Origin, shape.Radius),
            DangerKind.Line => LinePoints(shape),
            DangerKind.Cone => ConePoints(shape),
            _ => [],
        };

        if (world.Count < 3)
        {
            return;
        }

        var screen = new List<Vector2>(world.Count);
        foreach (var p in world)
        {
            if (!this.gameGui.WorldToScreen(new Vector3(p.X, groundY, p.Y), out var s))
            {
                return; // shape partly behind the camera — skip rather than smear
            }

            screen.Add(s);
        }

        var pts = screen.ToArray();
        fixed (Vector2* p = pts)
        {
            draw.AddConvexPolyFilled(p, pts.Length, DangerFill);
        }

        for (var i = 0; i < pts.Length; i++)
        {
            draw.AddLine(pts[i], pts[(i + 1) % pts.Length], DangerEdge, 2f);
        }
    }

    private void DrawDodge(ImDrawListPtr draw, CrucibleState state, DodgePlan plan, float groundY)
    {
        if (!plan.ShouldMove)
        {
            return;
        }

        if (!this.gameGui.WorldToScreen(state.PlayerPosition, out var from))
        {
            return;
        }

        if (plan.NoSafeSpot)
        {
            draw.AddCircleFilled(from, 10f, NoSafe, 16);
            return;
        }

        if (!this.gameGui.WorldToScreen(plan.TargetXZ, out var to))
        {
            return;
        }

        draw.AddLine(from, to, SafeArrow, 5f);
        draw.AddCircleFilled(to, 7f, SafeArrow, 16);

        // Arrowhead.
        var dir = Vector2.Normalize(to - from);
        var normal = new Vector2(-dir.Y, dir.X);
        draw.AddLine(to, to - (dir * 16f) + (normal * 9f), SafeArrow, 5f);
        draw.AddLine(to, to - (dir * 16f) - (normal * 9f), SafeArrow, 5f);
    }

    private static List<Vector2> CirclePoints(Vector2 centre, float radius)
    {
        var pts = new List<Vector2>(CircleSegments);
        for (var i = 0; i < CircleSegments; i++)
        {
            var a = MathF.Tau * i / CircleSegments;
            pts.Add(centre + new Vector2(MathF.Sin(a), MathF.Cos(a)) * radius);
        }

        return pts;
    }

    private static List<Vector2> LinePoints(DangerShape shape)
    {
        var side = new Vector2(shape.Forward.Y, -shape.Forward.X) * shape.HalfWidth;
        var tip = shape.Origin + (shape.Forward * shape.Radius);
        return
        [
            shape.Origin - side,
            shape.Origin + side,
            tip + side,
            tip - side,
        ];
    }

    private static List<Vector2> ConePoints(DangerShape shape)
    {
        var pts = new List<Vector2> { shape.Origin };
        var baseAngle = MathF.Atan2(shape.Forward.X, shape.Forward.Y);
        for (var i = 0; i <= ConeSegments; i++)
        {
            var a = baseAngle - shape.HalfAngle + (2f * shape.HalfAngle * i / ConeSegments);
            pts.Add(shape.Origin + new Vector2(MathF.Sin(a), MathF.Cos(a)) * shape.Radius);
        }

        return pts;
    }
}
