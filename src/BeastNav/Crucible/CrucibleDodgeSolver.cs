using System.Numerics;

namespace BeastNav.Crucible;

/// <summary>
/// Turns the currently-casting enemies into danger shapes and finds the nearest
/// spot clear of all of them. Geometry only — it returns a point and a
/// direction; it does not move the character.
/// </summary>
public sealed class CrucibleDodgeSolver
{
    // How far out to look for safety, and how finely.
    private const float MaxSearch = 22f;
    private const float RingStep = 1.5f;
    private const int Spokes = 24;

    // Padding added to every shape so we aim for clearly-safe ground.
    private const float Margin = 1.5f;

    // Default line half-width / cone half-angle when the sheet doesn't say.
    private const float DefaultLineHalfWidth = 3f;
    private const float DefaultConeHalfAngle = 1.0f; // ~57 degrees

    // A circle bigger than this can't be walked out of in an arena — treat it as
    // a raidwide: still shown, but not something to path around.
    private const float RaidwideRadius = 25f;

    private readonly CrucibleEncounterDatabase database;

    public CrucibleDodgeSolver(CrucibleEncounterDatabase database)
    {
        this.database = database;
    }

    public IReadOnlyList<DangerShape> LastShapes { get; private set; } = [];

    public DodgePlan LastPlan { get; private set; } = DodgePlan.Clear;

    public DodgePlan Solve(CrucibleState state)
    {
        this.LastPlan = this.SolveCore(state);
        return this.LastPlan;
    }

    private DodgePlan SolveCore(CrucibleState state)
    {
        if (!state.InCrucible || !state.HasPlayer)
        {
            this.LastShapes = [];
            return DodgePlan.Clear;
        }

        var shapes = new List<DangerShape>();
        foreach (var enemy in state.Enemies)
        {
            if (this.BuildShape(enemy) is { } shape)
            {
                shapes.Add(shape);
            }
        }

        this.LastShapes = shapes;
        if (shapes.Count == 0)
        {
            return DodgePlan.Clear;
        }

        // Raidwide-sized circles are drawn but excluded from the escape search.
        var avoidable = shapes
            .Where(s => s.Kind != DangerKind.Circle || s.Radius < RaidwideRadius)
            .ToList();
        if (avoidable.Count == 0)
        {
            return new DodgePlan { ThreatCount = shapes.Count, SecondsLeft = shapes.Min(s => s.SecondsLeft) };
        }

        var player = Flat(state.PlayerPosition);
        var inDanger = avoidable.Where(s => s.Contains(player)).ToList();
        var soonest = avoidable.Min(s => s.SecondsLeft);

        if (inDanger.Count == 0)
        {
            // Standing safe. Still report the shapes for drawing.
            return new DodgePlan { ThreatCount = shapes.Count, SecondsLeft = soonest };
        }

        // Search rings outward for the closest point clear of every shape.
        for (var r = RingStep; r <= MaxSearch; r += RingStep)
        {
            Vector2? best = null;
            var bestScore = float.MaxValue;

            for (var i = 0; i < Spokes; i++)
            {
                var a = MathF.Tau * i / Spokes;
                var candidate = player + new Vector2(MathF.Sin(a), MathF.Cos(a)) * r;
                if (avoidable.Any(s => s.Contains(candidate)))
                {
                    continue;
                }

                // Prefer the candidate that also keeps us near the pack (so we
                // don't sprint to a far wall for a small sidestep).
                var score = r + (0.15f * DistanceToNearestEnemy(candidate, state));
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (best is { } target)
            {
                var dir = Vector2.Normalize(target - player);
                return new DodgePlan
                {
                    ShouldMove = true,
                    TargetXZ = new Vector3(target.X, state.PlayerPosition.Y, target.Y),
                    Direction = dir,
                    Distance = Vector2.Distance(player, target),
                    SecondsLeft = inDanger.Min(s => s.SecondsLeft),
                    ThreatCount = inDanger.Count,
                };
            }
        }

        // Nowhere clear within range — flag it, no direction.
        return new DodgePlan
        {
            ShouldMove = true,
            NoSafeSpot = true,
            SecondsLeft = inDanger.Min(s => s.SecondsLeft),
            ThreatCount = inDanger.Count,
        };
    }

    private DangerShape? BuildShape(CrucibleEnemy enemy)
    {
        if (!enemy.IsCasting || enemy.CastActionId == 0)
        {
            return null;
        }

        var g = this.database.ReadGeometry(enemy.CastActionId);
        var origin = Flat(enemy.Position);
        var forward = new Vector2(MathF.Sin(enemy.Rotation), MathF.Cos(enemy.Rotation));

        return g.CastType switch
        {
            2 or 5 => new DangerShape
            {
                Kind = DangerKind.Circle,
                Origin = origin,
                Radius = MathF.Max(3f, g.Size) + Margin,
                SecondsLeft = enemy.CastRemaining,
                Name = enemy.Name,
            },
            4 or 12 => new DangerShape
            {
                Kind = DangerKind.Line,
                Origin = origin,
                Forward = forward,
                Radius = MathF.Max(10f, g.Size),
                HalfWidth = (g.HalfWidth > 0 ? g.HalfWidth : DefaultLineHalfWidth) + Margin,
                SecondsLeft = enemy.CastRemaining,
                Name = enemy.Name,
            },
            3 or 11 or 13 => new DangerShape
            {
                Kind = DangerKind.Cone,
                Origin = origin,
                Forward = forward,
                Radius = MathF.Max(5f, g.Size) + Margin,
                HalfAngle = DefaultConeHalfAngle,
                SecondsLeft = enemy.CastRemaining,
                Name = enemy.Name,
            },
            _ => null,
        };
    }

    private static float DistanceToNearestEnemy(Vector2 p, CrucibleState state)
    {
        var min = float.MaxValue;
        foreach (var enemy in state.Enemies)
        {
            min = MathF.Min(min, Vector2.Distance(p, Flat(enemy.Position)));
        }

        return min == float.MaxValue ? 0f : min;
    }

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Z);
}

public enum DangerKind
{
    Circle,
    Line,
    Cone,
}

/// <summary>A danger area in the arena's XZ plane.</summary>
public sealed record DangerShape
{
    public DangerKind Kind { get; init; }

    /// <summary>Caster position, XZ.</summary>
    public Vector2 Origin { get; init; }

    /// <summary>Unit forward vector for line / cone.</summary>
    public Vector2 Forward { get; init; }

    /// <summary>Circle radius, line length, or cone radius (yalms).</summary>
    public float Radius { get; init; }

    /// <summary>Line half-width (yalms).</summary>
    public float HalfWidth { get; init; }

    /// <summary>Cone half-angle (radians).</summary>
    public float HalfAngle { get; init; }

    public float SecondsLeft { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool Contains(Vector2 p)
    {
        var rel = p - this.Origin;
        return this.Kind switch
        {
            DangerKind.Circle => rel.LengthSquared() <= this.Radius * this.Radius,
            DangerKind.Line => LineContains(rel),
            DangerKind.Cone => ConeContains(rel),
            _ => false,
        };

        bool LineContains(Vector2 r)
        {
            var along = Vector2.Dot(r, this.Forward);
            if (along < 0f || along > this.Radius)
            {
                return false;
            }

            var side = Vector2.Dot(r, new Vector2(this.Forward.Y, -this.Forward.X));
            return MathF.Abs(side) <= this.HalfWidth;
        }

        bool ConeContains(Vector2 r)
        {
            var d = r.Length();
            if (d < 0.01f)
            {
                return true;
            }

            if (d > this.Radius)
            {
                return false;
            }

            var cos = Vector2.Dot(r / d, this.Forward);
            return cos >= MathF.Cos(this.HalfAngle);
        }
    }
}

public sealed record DodgePlan
{
    public static DodgePlan Clear { get; } = new();

    /// <summary>The player is standing in a danger area and should move.</summary>
    public bool ShouldMove { get; init; }

    /// <summary>Set when no clear ground was found within search range.</summary>
    public bool NoSafeSpot { get; init; }

    /// <summary>World point to move to (Y copied from the player).</summary>
    public Vector3 TargetXZ { get; init; }

    /// <summary>Unit XZ direction to the safe point.</summary>
    public Vector2 Direction { get; init; }

    public float Distance { get; init; }

    public float SecondsLeft { get; init; }

    public int ThreatCount { get; init; }
}
