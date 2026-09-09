using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BeastNav.Models;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BeastNav.Services;

/// <summary>
/// Reads the Beastmaster monster note ("図鑑") capture state and mirrors it into
/// <see cref="Configuration.TamedPetRowIds"/>.
/// </summary>
/// <remarks>
/// Two strategies are attempted, most reliable first:
/// <list type="number">
///   <item>
///     <b>addon</b> – while <c>XBMMonsterNotebook</c> is open, read its grid from
///     the AtkValues. Each cell's portrait icon id tells captured (unique art)
///     from uncaptured (a shared "locked" placeholder). The grid only feeds the
///     current page, so pages are folded into the list one at a time and the
///     running total is checked against the "&lt;captured&gt;/&lt;total&gt;" string.
///   </item>
///   <item>
///     <b>module</b> – serialize <see cref="XBMNoteModule"/> / <see cref="XBMModule"/>
///     and try conservative payload layouts as a fallback (currently diagnostic
///     only – <c>WriteFile</c> does not return usable data yet).
///   </item>
/// </list>
/// The manual <see cref="Configuration.MarkedPetRowIds"/> list is never touched.
/// </remarks>
public unsafe sealed class BeastTamingStateService
{
    private const uint MaxSnapshotBytes = 128 * 1024;
    private const int PreviewBytes = 96;

    private readonly IGameGui gameGui;
    private readonly BeastDataService beastData;
    private readonly Configuration configuration;
    private readonly IPluginLog log;

    public BeastTamingStateService(
        IGameGui gameGui,
        BeastDataService beastData,
        Configuration configuration,
        IPluginLog log)
    {
        this.gameGui = gameGui;
        this.beastData = beastData;
        this.configuration = configuration;
        this.log = log;
    }

    public BeastTamingSyncResult LastResult { get; private set; } = BeastTamingSyncResult.NotRun;

    /// <summary>Runs both strategies and applies the first confident result.</summary>
    /// <param name="allowUnverifiedMerge">
    /// When true (manual sync), a monster-note page whose count cannot be
    /// validated against the "x/total" string is still merged in. When false
    /// (auto sync), only a fully validated read is applied.
    /// </param>
    public BeastTamingSyncResult TrySync(bool allowUnverifiedMerge = true)
    {
        var addon = this.TryReadFromAddon(allowUnverifiedMerge);
        var result = addon.Success ? addon : this.MergeWithModule(addon);

        if (!result.Success)
        {
            this.LastResult = result;
            return result;
        }

        var desired = this.OrderByNo(result.TamedPetRowIds);
        var changed = !this.configuration.TamedPetRowIds.SequenceEqual(desired);
        if (changed)
        {
            this.configuration.TamedPetRowIds = desired;
        }

        // The strategy already built a precise message; only fill one in if it
        // left the field blank.
        result = result with
        {
            Synced = changed,
            Message = string.IsNullOrWhiteSpace(result.Message)
                ? (changed
                    ? $"Synced {desired.Count} captured monsters from {result.Strategy}."
                    : $"図鑑 already up to date ({desired.Count} captured).")
                : result.Message,
        };
        this.LastResult = result;
        return result;
    }

    private BeastTamingSyncResult MergeWithModule(BeastTamingSyncResult addonAttempt)
    {
        var module = this.TryReadFromModule();
        var diagnostics = addonAttempt.Diagnostics.Concat(module.Diagnostics).ToArray();
        if (module.Success)
        {
            return module with
            {
                Diagnostics = diagnostics,
            };
        }

        return module with
        {
            Diagnostics = diagnostics,
            Message = $"addon: {Fallback(addonAttempt.Message)} | module: {Fallback(module.Message)}",
        };

        static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    // ------------------------------------------------------------------ addon --

    private BeastTamingSyncResult TryReadFromAddon(bool allowUnverifiedMerge)
    {
        var pets = this.OrderedPets();
        if (pets.Length == 0)
        {
            return new BeastTamingSyncResult { Strategy = "addon", Message = "No XBMPet rows loaded." };
        }

        foreach (var name in this.configuration.BeastNoteAddonCandidates)
        {
            var addon = this.FindAddon(name);
            if (addon is null || !addon->IsVisible)
            {
                continue;
            }

            var values = ReadAtkValues(addon);
            var diagnostics = this.DescribeAddon(name, addon, includeNodes: true);
            return this.DecodeMonsterNotebook(name, values, pets, diagnostics, allowUnverifiedMerge);
        }

        return new BeastTamingSyncResult
        {
            Strategy = "addon",
            Message = "魔物図鑑が開いていません。図鑑を開いて再度同期してください。",
        };
    }

    // Shared "unknown monster" portrait shown for every uncaptured entry.
    private const long LockedIconFallback = 242051;

    /// <summary>
    /// Decodes the monster-note grid from its AtkValues. Each grid cell is an
    /// 8-value group anchored by a <c>String</c> "No.&lt;n&gt;" whose third value
    /// is the constant <c>UInt 3</c>. The value just before the anchor is the
    /// portrait icon id: captured entries have a unique portrait, uncaptured
    /// entries all share one "locked" placeholder icon. The grid only feeds the
    /// current page (25 rows), so results are folded into
    /// <see cref="Configuration.TamedPetRowIds"/> page by page.
    /// </summary>
    private BeastTamingSyncResult DecodeMonsterNotebook(
        string name,
        IReadOnlyList<AtkValueSnapshot> values,
        BeastPetLike[] pets,
        List<string> diagnostics,
        bool allowUnverifiedMerge)
    {
        var progress = values
            .Select(v => v.Text)
            .FirstOrDefault(t => !string.IsNullOrEmpty(t) && Regex.IsMatch(t, @"^\s*\d+\s*/\s*\d+\s*$"));
        int? expectedCaptured = null;
        int? total = null;
        if (progress is not null)
        {
            var m = Regex.Match(progress, @"(\d+)\s*/\s*(\d+)");
            expectedCaptured = int.Parse(m.Groups[1].Value);
            total = int.Parse(m.Groups[2].Value);
            diagnostics.Add($"progress string: {expectedCaptured}/{total}");
        }

        var rows = new List<(uint No, long Icon)>();
        for (var i = 1; i < values.Count; i++)
        {
            if (!values[i].Type.Equals("String", StringComparison.Ordinal))
            {
                continue;
            }

            var m = Regex.Match(values[i].Text ?? string.Empty, @"^\s*No\.\s*(\d+)\s*$");
            if (!m.Success || i + 2 >= values.Count || values[i + 2].Number != 3)
            {
                continue; // not a grid cell (e.g. the detail panel's ManagedString "No.1")
            }

            rows.Add((uint.Parse(m.Groups[1].Value), values[i - 1].Number));
        }

        if (rows.Count == 0)
        {
            return new BeastTamingSyncResult
            {
                Strategy = "addon",
                Source = name,
                Diagnostics = diagnostics,
                Message = $"{name} is open but no monster-note grid cells were found. Share /xllog.",
            };
        }

        // The locked placeholder icon is whichever icon id is shared by two or
        // more cells (real portraits are unique). Fall back to the known id.
        var lockedIcon = rows
            .GroupBy(r => r.Icon)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .Select(g => (long?)g.Key)
            .FirstOrDefault() ?? (rows.Any(r => r.Icon == LockedIconFallback) ? LockedIconFallback : (long?)null);

        diagnostics.Add($"parsed {rows.Count} grid cells No.{rows.Min(r => r.No)}-{rows.Max(r => r.No)}, lockedIcon={lockedIcon?.ToString() ?? "none"}");

        if (lockedIcon is null && !allowUnverifiedMerge)
        {
            return new BeastTamingSyncResult
            {
                Strategy = "addon",
                Source = name,
                Diagnostics = diagnostics,
                Message = $"{name}: every portrait on this page is unique; cannot tell captured from uncaptured. Open a page with uncaptured entries.",
            };
        }

        var capturedNos = rows
            .Where(r => lockedIcon is null || r.Icon != lockedIcon)
            .Select(r => r.No)
            .ToHashSet();
        var pageNos = rows.Select(r => r.No).ToHashSet();

        // A contiguous full run means this is an unfiltered page, so absent-from-
        // captured entries in that range can be authoritatively cleared.
        var min = pageNos.Min();
        var max = pageNos.Max();
        var contiguous = pageNos.Count == (int)(max - min + 1);

        var current = this.configuration.TamedPetRowIds.ToHashSet();
        foreach (var pet in pets)
        {
            if (capturedNos.Contains(pet.No))
            {
                current.Add(pet.PetRowId);
            }
            else if (contiguous && pet.No >= min && pet.No <= max)
            {
                current.Remove(pet.PetRowId);
            }
        }

        var merged = this.OrderByNo(current);
        var known = total is int t ? merged.Count(id => (this.beastData.FindPet(id)?.XbmRowId ?? uint.MaxValue) <= t) : merged.Count;
        var verified = expectedCaptured is int ec && known == ec;

        return new BeastTamingSyncResult
        {
            Success = true,
            Strategy = "addon",
            Source = name,
            Method = $"notebook portraits, page No.{min}-{max}{(contiguous ? string.Empty : " (filtered)")}",
            TamedPetRowIds = merged,
            Diagnostics = diagnostics,
            Message = verified
                ? $"図鑑 synced: {known}/{total} captured."
                : expectedCaptured is int ec2
                    ? $"Page No.{min}-{max} folded in: {known} known, 図鑑 says {ec2}/{total}. Flip to the other page(s) to finish."
                    : $"Page No.{min}-{max} folded in: {known} known so far.",
        };
    }

    // ----------------------------------------------------------------- module --

    private BeastTamingSyncResult TryReadFromModule()
    {
        try
        {
            var uiModulePtr = this.gameGui.GetUIModule();
            if (uiModulePtr.IsNull)
            {
                return new BeastTamingSyncResult { Strategy = "module", Message = "UIModule is unavailable." };
            }

            var uiModule = (UIModule*)uiModulePtr.Address;

            var noteModule = uiModule->GetXBMNoteModule();
            var note = noteModule is null
                ? new BeastTamingSyncResult { Strategy = "module", Source = "XBMNoteModule", Message = "XBMNoteModule is unavailable." }
                : this.ReadModuleSnapshot("XBMNoteModule", &noteModule->UserFileEvent);
            if (note.Success)
            {
                return note;
            }

            var mainModule = uiModule->GetXBMModule();
            var main = mainModule is null
                ? new BeastTamingSyncResult { Strategy = "module", Source = "XBMModule", Message = "XBMModule is unavailable." }
                : this.ReadModuleSnapshot("XBMModule", &mainModule->UserFileEvent);
            if (main.Success)
            {
                return main;
            }

            var diagnostics = note.Diagnostics.Concat(main.Diagnostics).ToArray();
            var richer = note.Diagnostics.Count >= main.Diagnostics.Count ? note : main;
            return richer with
            {
                Success = false,
                Diagnostics = diagnostics,
                Message = $"XBMNoteModule: {note.Message} | XBMModule: {main.Message}",
            };
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to read Beastmaster module state.");
            return new BeastTamingSyncResult
            {
                Strategy = "module",
                Message = $"Failed to read XBM module: {ex.GetType().Name}.",
            };
        }
    }

    private BeastTamingSyncResult ReadModuleSnapshot(string source, UserFileManager.UserFileEvent* module)
    {
        if (module is null)
        {
            return new BeastTamingSyncResult { Strategy = "module", Source = source, Message = $"{source} is unavailable." };
        }

        uint dataSize = 0;
        uint fileSize = 0;
        ushort fileTypeId = 0;
        ushort fileVersionId = 0;
        var hasChanges = false;
        var isVirtual = false;
        uint tempBytes = 0;
        var tempPtrSet = false;
        try
        {
            dataSize = module->GetDataSize();
            fileSize = module->GetFileSize();
            fileTypeId = module->GetFileType();
            fileVersionId = module->GetFileVersion();
            hasChanges = module->GetHasChanges();
            isVirtual = module->IsVirtual;
            tempBytes = module->TempDataBytesWritten;
            tempPtrSet = module->TempDataPtr != nint.Zero;
        }
        catch
        {
            // Fall through with whatever we managed to read.
        }

        var diagnostics = new List<string>
        {
            $"{source}: data={dataSize} file={fileSize} type=0x{fileTypeId:X4} ver={fileVersionId} "
            + $"hasChanges={hasChanges} virtual={isVirtual} tempBytes={tempBytes} tempPtr={tempPtrSet}",
        };

        // GetDataSize / GetFileSize return junk before the module has ever loaded
        // its server data. Pick a sane buffer: a reported size in range, else a
        // fixed probe buffer, and trust WriteFile's return value.
        uint bufferSize = dataSize is > 0 and <= MaxSnapshotBytes ? dataSize
            : fileSize is > 0 and <= MaxSnapshotBytes ? fileSize
            : 64 * 1024;

        var buffer = new byte[bufferSize];
        uint written = 0;
        try
        {
            fixed (byte* p = buffer)
            {
                written = module->WriteFile(p, bufferSize);
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add($"  WriteFile threw {ex.GetType().Name}");
            return new BeastTamingSyncResult
            {
                Strategy = "module",
                Source = source,
                FileSize = fileSize,
                DataSize = dataSize,
                Diagnostics = diagnostics,
                Message = "図鑑 module data is not available yet (WriteFile failed).",
            };
        }

        if (written == 0 || written > bufferSize)
        {
            diagnostics.Add($"  WriteFile returned {written} (buffer {bufferSize})");
            return new BeastTamingSyncResult
            {
                Strategy = "module",
                Source = source,
                FileSize = fileSize,
                DataSize = dataSize,
                Diagnostics = diagnostics,
                Message = "図鑑 module data is not loaded yet. Open the 魔物図鑑 and try again.",
            };
        }

        var data = buffer.AsSpan(0, (int)written).ToArray();
        diagnostics.Add($"  WriteFile wrote {written} bytes");
        diagnostics.Add($"  first 128 : {Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 128)))}");

        var baseResult = new BeastTamingSyncResult
        {
            Strategy = "module",
            Source = source,
            FileSize = fileSize,
            DataSize = dataSize,
            FileType = SafeAscii(data.AsSpan(0, Math.Min(data.Length, 4))),
            FileVersion = fileVersionId,
            HeaderLength = 0,
            DataHash = Convert.ToHexString(SHA256.HashData(data)),
            PreviewHex = Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, PreviewBytes))),
            PayloadHex = Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 256))),
            Diagnostics = diagnostics,
        };

        var pets = this.OrderedPets();

        // The exact header length is unknown, so try a few plausible offsets and
        // require every layout that matches to agree on the same monster set.
        var candidateHeaders = new List<int> { 0, 4, 8, 12, 16, 32 };
        if (fileSize is > 0 and <= MaxSnapshotBytes && dataSize is > 0 and < MaxSnapshotBytes
            && fileSize > dataSize && fileSize - dataSize < written)
        {
            candidateHeaders.Insert(0, (int)(fileSize - dataSize));
        }

        var layouts = new List<(string Method, List<uint> Tamed)>();
        foreach (var header in candidateHeaders.Distinct().Where(h => h >= 0 && h < data.Length))
        {
            var payload = data.AsSpan(header).ToArray();
            if (TryPackedBitset(payload, pets, out var byBits))
            {
                layouts.Add(($"packed bits, header {header}", byBits));
            }

            if (TryBytePerEntry(payload, pets, out var byBytes))
            {
                layouts.Add(($"byte per entry, header {header}", byBytes));
            }
        }

        var distinct = layouts
            .GroupBy(layout => string.Join(",", this.OrderByNo(layout.Tamed)))
            .ToArray();

        if (distinct.Length == 1)
        {
            var winner = distinct[0].First();
            return baseResult with
            {
                Success = true,
                Method = winner.Method,
                TamedPetRowIds = this.OrderByNo(winner.Tamed),
                Message = $"Decoded {winner.Tamed.Count} captured monsters ({winner.Method}).",
            };
        }

        if (distinct.Length == 0)
        {
            return baseResult with { Message = "Module data read but no known payload layout matched. Share /xllog." };
        }

        return baseResult with
        {
            Message = $"Ambiguous payload: {distinct.Length} candidate sets. Share /xllog.",
        };
    }

    private static bool TryPackedBitset(byte[] payload, BeastPetLike[] pets, out List<uint> tamed)
    {
        tamed = [];
        if (pets.Length == 0)
        {
            return false;
        }

        var maxIndex = pets.Max(pet => pet.No) - 1;
        var requiredBytes = (int)(maxIndex / 8) + 1;
        if (payload.Length < requiredBytes || AllEqual(payload.AsSpan(0, requiredBytes), 0xFF))
        {
            return false;
        }

        var hits = new List<uint>();
        foreach (var pet in pets)
        {
            var index = pet.No - 1;
            if ((payload[index / 8] & (1 << (int)(index % 8))) != 0)
            {
                hits.Add(pet.PetRowId);
            }
        }

        if (hits.Count is 0 || hits.Count == pets.Length)
        {
            return false;
        }

        tamed = hits;
        return true;
    }

    private static bool TryBytePerEntry(byte[] payload, BeastPetLike[] pets, out List<uint> tamed)
    {
        tamed = [];
        if (pets.Length == 0)
        {
            return false;
        }

        var maxIndex = (int)pets.Max(pet => pet.No) - 1;
        if (payload.Length <= maxIndex)
        {
            return false;
        }

        var hits = new List<uint>();
        foreach (var pet in pets)
        {
            var value = payload[(int)pet.No - 1];
            if (value > 1)
            {
                return false;
            }

            if (value == 1)
            {
                hits.Add(pet.PetRowId);
            }
        }

        if (hits.Count is 0 || hits.Count == pets.Length)
        {
            return false;
        }

        tamed = hits;
        return true;
    }

    // --------------------------------------------------------------- dump only --

    /// <summary>Logs every candidate addon's AtkValues and node tree for reverse engineering.</summary>
    public void DumpNoteAddons()
    {
        var any = false;
        var names = this.configuration.BeastNoteAddonCandidates
            .Append("XBMMonsterBookDetail")
            .Distinct();
        foreach (var name in names)
        {
            var addon = this.FindAddon(name);
            if (addon is null)
            {
                continue;
            }

            any = true;
            foreach (var line in this.DescribeAddon(name, addon, includeNodes: true))
            {
                this.log.Information("[BeastHelper] dumpnote {Line}", line);
            }
        }

        if (!any)
        {
            this.log.Information("[BeastHelper] dumpnote: none of the candidate addons are loaded. Open the 魔物図鑑 first.");
        }
    }

    private List<string> DescribeAddon(string name, AtkUnitBase* addon, bool includeNodes)
    {
        var lines = new List<string> { $"{name}: visible={addon->IsVisible}" };

        var values = ReadAtkValues(addon);
        lines.Add($"  {values.Count} AtkValues");
        for (var i = 0; i < values.Count; i++)
        {
            lines.Add($"    v[{i}] {values[i]}");
        }

        if (includeNodes)
        {
            try
            {
                WalkComponentNodes(&addon->UldManager, 1, lines);
            }
            catch (Exception ex)
            {
                lines.Add($"  node walk failed: {ex.GetType().Name}");
            }
        }

        return lines;
    }

    private static void WalkComponentNodes(AtkUldManager* uld, int depth, List<string> lines)
    {
        if (uld is null || depth > 6)
        {
            return;
        }

        var indent = new string(' ', depth * 2);
        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node is null)
            {
                continue;
            }

            var typeValue = (uint)node->Type;
            if (typeValue == (uint)NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                var text = textNode->NodeText.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    lines.Add($"{indent}text#{node->NodeId} '{text.Replace('\n', ' ')}'");
                }

                continue;
            }

            if (typeValue < 1000)
            {
                continue;
            }

            var componentNode = (AtkComponentNode*)node;
            var component = componentNode->Component;
            if (component is null)
            {
                continue;
            }

            var componentType = component->GetComponentType();
            lines.Add($"{indent}component#{node->NodeId} {componentType} visible={node->IsVisible()}");

            switch (componentType)
            {
                case ComponentType.List:
                {
                    var list = (AtkComponentList*)component;
                    lines.Add($"{indent}  ListLength={list->ListLength} visibleRows={list->NumVisibleRows} visibleItems={list->NumVisibleItems}");
                    break;
                }

                case ComponentType.XBMItem:
                {
                    var item = (AtkComponentXBMItem*)component;
                    lines.Add($"{indent}  XBMItemId={item->XBMItemId} loaded={item->IsItemLoaded} name='{item->NameText.ToString().Replace('\n', ' ')}' type='{item->TypeText.ToString().Replace('\n', ' ')}'");
                    break;
                }
            }

            WalkComponentNodes(&component->UldManager, depth + 1, lines);
        }
    }

    // ------------------------------------------------------------------ shared --

    private AtkUnitBase* FindAddon(string name)
    {
        try
        {
            var ptr = this.gameGui.GetAddonByName(name, 1);
            return ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        }
        catch
        {
            return null;
        }
    }

    private BeastPetLike[] OrderedPets()
        => this.beastData.Pets
            .OrderBy(pet => pet.XbmRowId)
            .Select(pet => new BeastPetLike(pet.XbmRowId, pet.PetRowId))
            .ToArray();

    private List<uint> OrderByNo(IEnumerable<uint> petRowIds)
        => petRowIds
            .Distinct()
            .OrderBy(rowId => this.beastData.FindPet(rowId)?.XbmRowId ?? uint.MaxValue)
            .ThenBy(rowId => rowId)
            .ToList();

    private static List<AtkValueSnapshot> ReadAtkValues(AtkUnitBase* addon)
    {
        var result = new List<AtkValueSnapshot>();
        var count = addon->AtkValuesCount;
        var values = addon->AtkValues;
        if (values is null)
        {
            return result;
        }

        for (var i = 0; i < count && i < 4096; i++)
        {
            result.Add(AtkValueSnapshot.From(&values[i]));
        }

        return result;
    }

    private static string SafeAscii(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }

        return sb.ToString();
    }

    private static bool AllEqual(ReadOnlySpan<byte> values, byte expected)
    {
        foreach (var value in values)
        {
            if (value != expected)
            {
                return false;
            }
        }

        return values.Length > 0;
    }

    private readonly record struct BeastPetLike(uint No, uint PetRowId);

    private readonly record struct AtkValueSnapshot(string Type, long Number, string Text)
    {
        public static AtkValueSnapshot From(AtkValue* value)
        {
            if (value is null)
            {
                return new AtkValueSnapshot("null", long.MinValue, string.Empty);
            }

            // The AtkValue.Type enum has moved namespaces between FFXIVClientStructs
            // versions, so switch on its name instead of the enum members.
            var type = value->Type.ToString();
            try
            {
                switch (type)
                {
                    case "Int":
                        return new AtkValueSnapshot(type, value->Int, value->Int.ToString());
                    case "UInt":
                        return new AtkValueSnapshot(type, value->UInt, value->UInt.ToString());
                    case "Int64":
                        return new AtkValueSnapshot(type, value->Int64, value->Int64.ToString());
                    case "UInt64":
                        return new AtkValueSnapshot(type, (long)value->UInt64, value->UInt64.ToString());
                    case "Bool":
                        return new AtkValueSnapshot(type, value->Byte, value->Byte != 0 ? "true" : "false");
                    case "Float":
                        return new AtkValueSnapshot(type, (long)value->Float, value->Float.ToString("0.###"));
                    case "String":
                    case "String8":
                    case "ConstString":
                    case "ManagedString":
                        var raw = (byte*)value->String;
                        var text = raw != null ? MemoryHelper.ReadStringNullTerminated((nint)raw) : string.Empty;
                        return new AtkValueSnapshot(type, long.MinValue, text.Replace('\n', ' '));
                    default:
                        return new AtkValueSnapshot(type, long.MinValue, string.Empty);
                }
            }
            catch
            {
                return new AtkValueSnapshot(type, long.MinValue, "<error>");
            }
        }

        public override string ToString()
            => Number == long.MinValue
                ? $"{Type}={(string.IsNullOrEmpty(Text) ? "?" : Text)}"
                : $"{Type}={Number}";
    }
}
