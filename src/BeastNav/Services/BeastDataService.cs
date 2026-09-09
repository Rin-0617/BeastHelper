using BeastNav.Models;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Data;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System.Reflection;

namespace BeastNav.Services;

public sealed class BeastDataService
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private IReadOnlyList<BeastPet> pets = [];

    public BeastDataService(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
        this.Reload();
    }

    public IReadOnlyList<BeastPet> Pets => this.pets;

    public BeastPet? FindPet(uint petRowId)
        => this.pets.FirstOrDefault(pet => pet.PetRowId == petRowId);

    public BeastPet? FindPetByXbmRowId(uint xbmRowId)
        => this.pets.FirstOrDefault(pet => pet.XbmRowId == xbmRowId);

    public void Reload()
    {
        try
        {
            this.pets = this.LoadPets();
        }
        catch (Exception ex)
        {
            this.pets = [];
            this.log.Error(ex, "Failed to load XBMPet data.");
        }
    }

    private IReadOnlyList<BeastPet> LoadPets()
    {
        var language = ToLuminaLanguage(this.dataManager.Language);
        var petNames = this.LoadPetNames();
        var sheet = this.LoadRawXbmPetSheet(language);
        var rows = new List<BeastPet>();
        var createRowAt = sheet.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method =>
                method.Name == "UnsafeCreateRowAt" &&
                method.IsGenericMethodDefinition &&
                method.GetParameters().Length == 1)
            ?? throw new MissingMethodException(sheet.GetType().FullName, "UnsafeCreateRowAt<T>(int)");
        var createRawRowAt = createRowAt
            .MakeGenericMethod(typeof(RawRow));

        for (var rowIndex = 0; rowIndex < sheet.Count; rowIndex++)
        {
            var row = (RawRow)createRawRowAt.Invoke(sheet, [rowIndex])!;
            var petRowId = row.ReadUInt32Column(0);
            if (petRowId == 0)
            {
                continue;
            }

            petNames.TryGetValue(petRowId, out var name);

            rows.Add(new BeastPet
            {
                XbmRowId = row.RowId,
                PetRowId = petRowId,
                Name = string.IsNullOrWhiteSpace(name) ? $"Pet#{petRowId}" : name,
                Description = row.ReadStringColumn(8).ToString(),
                IconId = row.ReadUInt32Column(4),
                Element = row.ReadUInt8Column(6),
                Rank = row.ReadUInt8Column(1),
                UnlockValue = row.ReadUInt16Column(7),
                Stat1 = row.ReadUInt8Column(22),
                Stat2 = row.ReadUInt8Column(23),
                Stat3 = row.ReadUInt8Column(24),
                Stat4 = row.ReadUInt8Column(25),
                Stat5 = row.ReadUInt8Column(26),
            });
        }

        var loadedRows = rows.OrderBy(row => row.XbmRowId).ToArray();
        this.log.Information("[BeastHelper] Loaded {Count} XBMPet rows.", loadedRows.Length);
        return loadedRows;
    }

    private RawExcelSheet LoadRawXbmPetSheet(Language language)
    {
        Exception? lastException = null;
        var tried = new HashSet<string>(StringComparer.Ordinal);
        Language?[] candidates = [language, null, Language.None, Language.English];

        foreach (var candidate in candidates)
        {
            var key = candidate?.ToString() ?? "<default>";
            if (!tried.Add(key))
            {
                continue;
            }

            try
            {
                return this.dataManager.Excel.GetRawSheet("XBMPet", candidate);
            }
            catch (Exception ex)
            {
                lastException = ex;
                this.log.Debug(ex, "Failed to load XBMPet raw sheet with language {Language}.", key);
            }
        }

        throw new InvalidOperationException("Failed to load XBMPet raw sheet with all language fallbacks.", lastException);
    }

    private Dictionary<uint, string> LoadPetNames()
    {
        var sheet = this.dataManager.GetExcelSheet<Pet>(this.dataManager.Language);
        if (sheet is null)
        {
            return [];
        }

        var names = new Dictionary<uint, string>();
        foreach (var row in sheet)
        {
            var name = row.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                names[row.RowId] = name;
            }
        }

        return names;
    }

    private static Language ToLuminaLanguage(ClientLanguage language)
        => language switch
        {
            ClientLanguage.Japanese => Language.Japanese,
            ClientLanguage.English => Language.English,
            ClientLanguage.German => Language.German,
            ClientLanguage.French => Language.French,
            _ => Language.None,
        };
}
