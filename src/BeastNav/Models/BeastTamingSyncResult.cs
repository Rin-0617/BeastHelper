namespace BeastNav.Models;

public sealed record BeastTamingSyncResult
{
    public static BeastTamingSyncResult NotRun { get; } = Failure("Not run yet.");

    public DateTime Time { get; init; } = DateTime.Now;

    public bool Success { get; init; }

    public bool Synced { get; init; }

    /// <summary>"addon", "module", or "-".</summary>
    public string Strategy { get; init; } = "-";

    public string Source { get; init; } = "-";

    public uint FileSize { get; init; }

    public uint DataSize { get; init; }

    public string FileType { get; init; } = string.Empty;

    public uint FileVersion { get; init; }

    public int HeaderLength { get; init; }

    public string DataHash { get; init; } = string.Empty;

    public string PreviewHex { get; init; } = string.Empty;

    public string PayloadHex { get; init; } = string.Empty;

    public IReadOnlyList<uint> TamedPetRowIds { get; init; } = [];

    public string Method { get; init; } = "-";

    public string Message { get; init; } = string.Empty;

    /// <summary>Free-form lines captured for reverse engineering / support.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    public static BeastTamingSyncResult Failure(string message)
        => new()
        {
            Success = false,
            Message = message,
        };
}
