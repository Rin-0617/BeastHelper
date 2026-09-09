namespace BeastNav.Models;

public sealed record BeastPet
{
    public uint XbmRowId { get; init; }

    public uint PetRowId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public uint IconId { get; init; }

    public byte Element { get; init; }

    public byte Rank { get; init; }

    public ushort UnlockValue { get; init; }

    public byte Stat1 { get; init; }

    public byte Stat2 { get; init; }

    public byte Stat3 { get; init; }

    public byte Stat4 { get; init; }

    public byte Stat5 { get; init; }
}
