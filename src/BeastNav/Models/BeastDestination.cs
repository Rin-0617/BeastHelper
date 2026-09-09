using System.Numerics;

namespace BeastNav.Models;

public sealed record BeastDestination
{
    public uint XbmRowId { get; init; }

    public uint PetRowId { get; init; }

    public uint TerritoryId { get; init; }

    public uint MapId { get; init; }

    public float? MapX { get; init; }

    public float? MapY { get; init; }

    public float X { get; init; }

    public float Y { get; init; }

    public float Z { get; init; }

    public float Radius { get; init; } = 7.5f;

    public ushort EnemyLevelMin { get; init; }

    public ushort EnemyLevelMax { get; init; }

    public List<string> TargetNames { get; init; } = [];

    public string Label { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public bool Approximate { get; init; }

    public Vector3 Position => new(this.X, this.Y, this.Z);
}
