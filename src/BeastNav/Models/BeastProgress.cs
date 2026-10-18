namespace BeastNav.Models;

/// <summary>A single beast's level/exp, as last seen on its 図鑑 detail panel.</summary>
public sealed record BeastProgress
{
    public int Level { get; init; }

    public int Exp { get; init; }

    public int MaxExp { get; init; }
}
