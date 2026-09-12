using Dalamud.Configuration;

namespace BeastNav;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public bool Fly { get; set; }

    public float StopDistance { get; set; } = 7.5f;

    // --- Manual "I don't need this pet" list (user controlled) ---------------
    // Toggled from the pet list. Never written by the bestiary sync.
    public List<uint> MarkedPetRowIds { get; set; } = [];

    // When set, the pet list hides entries that are captured in the 図鑑 or
    // manually marked.
    public bool HideCompletedPets { get; set; }

    // --- Bestiary ("図鑑") capture state, synced from the game ---------------
    // Populated by BeastTamingStateService. Treat as read-only mirror of the
    // in-game monster note; editing by hand will be overwritten on the next sync.
    // On by default: the XBMManager source makes this cheap and it needs no
    // open 魔物図鑑.
    public bool AutoSyncBeastNote { get; set; } = true;

    public List<uint> TamedPetRowIds { get; set; } = [];

    // Addon names that render the monster-note list. Used by the bestiary sync
    // to scrape capture state while the window is open.
    public List<string> BeastNoteAddonCandidates { get; set; } =
    [
        "XBMMonsterNotebook",
        "XBMMonsterList",
        "XBMMonsterBook",
        "XBMNote",
        "XBMNotebook",
    ];

}
