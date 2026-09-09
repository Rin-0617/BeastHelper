using System.Text.Json;
using System.Text.Json.Serialization;
using BeastNav.Models;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace BeastNav.Services;

public sealed class BeastDestinationService
{
    private const string FileName = "beast-destinations.json";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IDataManager dataManager;
    private readonly BeastDataService beastData;
    private readonly IPluginLog log;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private List<BeastDestination> destinations = [];
    private int manualCount;
    private int builtInCount;

    public BeastDestinationService(
        IDalamudPluginInterface pluginInterface,
        IDataManager dataManager,
        BeastDataService beastData,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.dataManager = dataManager;
        this.beastData = beastData;
        this.log = log;
        this.Reload();
    }

    public string ConfigPath => Path.Combine(this.pluginInterface.ConfigDirectory.FullName, FileName);

    public IReadOnlyList<BeastDestination> All => this.destinations;

    public int ManualCount => this.manualCount;

    public int BuiltInCount => this.builtInCount;

    public void Reload()
    {
        Directory.CreateDirectory(this.pluginInterface.ConfigDirectory.FullName);

        var manual = this.LoadManualDestinations();
        var builtIn = BeastmasterFieldSource.CreateDestinations(this.dataManager, this.beastData.Pets);

        var manualPetIds = manual.Select(static destination => destination.PetRowId).ToHashSet();
        this.manualCount = manual.Count;
        this.builtInCount = builtIn.Count(destination => !manualPetIds.Contains(destination.PetRowId));
        this.destinations = manual
            .Concat(builtIn.Where(destination => !manualPetIds.Contains(destination.PetRowId)))
            .ToList();
    }

    public BeastDestination? Find(uint petRowId)
        => this.destinations.FirstOrDefault(destination => destination.PetRowId == petRowId);

    private List<BeastDestination> LoadManualDestinations()
    {
        if (!File.Exists(this.ConfigPath))
        {
            this.WriteTemplate();
            return [];
        }

        try
        {
            var text = File.ReadAllText(this.ConfigPath);
            var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var entries = root.ValueKind == JsonValueKind.Array
                ? root.Deserialize<List<BeastDestination>>(this.jsonOptions)
                : root.TryGetProperty("destinations", out var destinationsElement)
                    ? destinationsElement.Deserialize<List<BeastDestination>>(this.jsonOptions)
                    : [];

            return (entries ?? [])
                .Select(this.NormalizeManualDestination)
                .Where(static destination => destination.PetRowId != 0)
                .ToList();
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to load BeastHelper destinations.");
            return [];
        }
    }

    private BeastDestination NormalizeManualDestination(BeastDestination destination)
    {
        if (destination.PetRowId == 0 && destination.XbmRowId != 0)
        {
            var pet = this.beastData.Pets.FirstOrDefault(row => row.XbmRowId == destination.XbmRowId);
            if (pet is not null)
            {
                destination = destination with { PetRowId = pet.PetRowId };
            }
        }

        if (destination.MapX is null || destination.MapY is null || destination.MapId == 0)
        {
            return destination;
        }

        var mapSheet = this.dataManager.GetExcelSheet<Map>();
        var map = mapSheet?.FirstOrDefault(row => row.RowId == destination.MapId);
        if (map is null || map.Value.RowId == 0)
        {
            return destination;
        }

        var position = BeastmasterFieldSource.MapToWorld(
            destination.MapX.Value,
            destination.MapY.Value,
            map.Value.SizeFactor,
            map.Value.OffsetX,
            map.Value.OffsetY);

        return destination with
        {
            TerritoryId = destination.TerritoryId == 0 ? map.Value.TerritoryType.RowId : destination.TerritoryId,
            X = position.X,
            Y = destination.Y,
            Z = position.Z,
        };
    }

    private void WriteTemplate()
    {
        const string comment =
            "Manual entries override the built-in coordinates. " +
            "Use mapX/mapY with mapId for player-visible coordinates, or x/y/z for raw world coordinates.";

        var template = new DestinationFile(comment, []);
        File.WriteAllText(this.ConfigPath, JsonSerializer.Serialize(template, this.jsonOptions));
    }

    private sealed record DestinationFile(string Comment, List<BeastDestination> Destinations);
}
