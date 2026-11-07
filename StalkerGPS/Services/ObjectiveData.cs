using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace StalkerGPS.Services;

/// <summary>One place on the map that an objective can point at.</summary>
public sealed record ObjectiveLocation(string Id, string JournalQuest, double X, double Y);

/// <summary>
/// What the game's quest markers can point at: the places objectives are at, the objectives that
/// follow each object, by its GUID, and each journal quest's stages in the game's order, as a marker
/// refers to them. A stage the map doesn't have is an empty string.
/// </summary>
public sealed record ObjectiveMap(List<ObjectiveLocation> Locations, Dictionary<string, List<ActiveObjective>> Targets, Dictionary<string, List<string>> Stages)
{
    /// <summary>The objective at a place in a journal quest, if the map has it.</summary>
    public ActiveObjective? StageAt(string journalQuest, int stage) =>
        Stages.TryGetValue(journalQuest, out var stages) && stage >= 0 && stage < stages.Count && stages[stage].Length > 0
            ? new ActiveObjective(stages[stage], journalQuest)
            : null;
}

/// <summary>
/// Where each objective on the stalker.vodka map points, which is how the game's quest markers are
/// matched to objectives. Downloaded from the site and cached so it also works offline.
/// </summary>
public static class ObjectiveData
{
    private const string DataUrl = "https://assets.stalker.vodka/data/quests.json";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StalkerGPS", "objectives.json");

    private static readonly ObjectiveMap Empty = new([], [], []);

    /// <summary>Downloads the map, or returns the cached copy if the site can't be reached.</summary>
    public static async Task<ObjectiveMap> LoadAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = DownloadTimeout };
            await using var stream = await http.GetStreamAsync(DataUrl, ct);
            using var quests = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var map = Parse(quests.RootElement);
            if (map.Locations.Count > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                await File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(map), ct);
                return map;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException or KeyNotFoundException && !ct.IsCancellationRequested)
        {
            // Offline or the data changed shape; fall back to the last good copy.
        }

        try
        {
            return File.Exists(CachePath)
                ? JsonSerializer.Deserialize<ObjectiveMap>(await File.ReadAllTextAsync(CachePath, ct)) ?? Empty
                : Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return Empty;
        }
    }

    private static ObjectiveMap Parse(JsonElement quests)
    {
        var locations = new HashSet<ObjectiveLocation>();
        var targets = new Dictionary<string, SortedSet<(string JournalQuest, string Id)>>();
        var order = new Dictionary<string, List<string>>();
        foreach (var quest in quests.EnumerateArray())
        {
            if (!quest.TryGetProperty("stages", out var stages) || stages.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var stage in stages.EnumerateArray())
            {
                var id = stage.GetProperty("id").GetString()!;
                var journalQuest = stage.GetProperty("journalQuest").GetString()!;
                if (stage.TryGetProperty("journalIndex", out var index) && index.ValueKind == JsonValueKind.Number)
                {
                    if (!order.TryGetValue(journalQuest, out var ordered))
                        order[journalQuest] = ordered = [];
                    while (ordered.Count <= index.GetInt32())
                        ordered.Add("");
                    ordered[index.GetInt32()] = id;
                }
                if (stage.TryGetProperty("markers", out var markers) && markers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var marker in markers.EnumerateArray())
                        locations.Add(new ObjectiveLocation(id, journalQuest, marker.GetProperty("X").GetDouble(), marker.GetProperty("Y").GetDouble()));
                }
                if (stage.TryGetProperty("markerTargets", out var objects) && objects.ValueKind == JsonValueKind.Array)
                {
                    foreach (var guid in objects.EnumerateArray().Select(o => o.GetString()!))
                    {
                        if (!targets.TryGetValue(guid, out var followers))
                            targets[guid] = followers = [];
                        followers.Add((journalQuest, id));
                    }
                }
            }
        }
        return new ObjectiveMap([.. locations],
            targets.ToDictionary(t => t.Key, t => t.Value.Select(s => new ActiveObjective(s.Id, s.JournalQuest)).ToList()), order);
    }
}
