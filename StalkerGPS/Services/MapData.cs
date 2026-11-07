using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace StalkerGPS.Services;

/// <summary>
/// The IDs of the objects on the stalker.vodka map whose state the app tracks: the GUIDs of
/// doors (locks of kind "door"), of world spawns (items' "foundAt" entries) and which of those
/// spawns only appear through a quest, of stashes, and of the other locks with a GUID (devices such
/// as bunker doors and levers that a quest's signal switches on). They tell <see cref="WorldStateTracker"/>
/// what to look for. Downloaded from the site and cached so tracking also works offline.
/// </summary>
public sealed record MapData(IReadOnlyList<string> DoorGuids, IReadOnlyList<string> SpawnIds, IReadOnlyList<string> QuestSpawnIds,
    IReadOnlyList<string> StashGuids, IReadOnlyList<string>? DeviceGuids = null)
{
    private const string DataUrl = "https://assets.stalker.vodka/data/";
    // Keys are in "items", as they are for the website; "keys.json" is an old file that is no longer updated.
    private static readonly string[] ItemFiles = ["weapons", "armor", "artifacts", "attachments", "items"];
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StalkerGPS", "map-ids.json");

    public static MapData Empty { get; } = new([], [], [], [], []);

    /// <summary>The GUIDs of locks other than doors; never null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Devices => DeviceGuids ?? [];

    public bool IsEmpty => DoorGuids.Count == 0 && SpawnIds.Count == 0 && StashGuids.Count == 0;

    /// <summary>Downloads the IDs, or returns the cached copy if the site can't be reached.</summary>
    public static async Task<MapData> LoadAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = DownloadTimeout };
            List<string> doors, devices;
            using (var locks = await DownloadAsync(http, "locks", ct))
            {
                static bool IsDoor(JsonElement l) => l.TryGetProperty("kind", out var kind) && kind.GetString() == "door";
                static string? Guid(JsonElement l) => l.TryGetProperty("guid", out var guid) && guid.ValueKind == JsonValueKind.String ? guid.GetString() : null;
                doors = Ids(locks.RootElement.EnumerateArray().Where(IsDoor).Select(Guid));
                devices = Ids(locks.RootElement.EnumerateArray().Where(l => !IsDoor(l)).Select(Guid));
            }
            List<string> stashes;
            using (var stashData = await DownloadAsync(http, "stashes", ct))
            {
                // Named like the locks' "guid" or the spawns' "id"; either is accepted.
                stashes = Ids(stashData.RootElement.EnumerateArray()
                    .Select(s => s.TryGetProperty("guid", out var guid) ? guid.GetString()
                        : s.TryGetProperty("id", out var id) ? id.GetString() : null));
            }
            var spawns = new List<string>();
            var questSpawns = new List<string>();
            foreach (var file in ItemFiles)
            {
                using var items = await DownloadAsync(http, file, ct);
                var spots = items.RootElement.EnumerateArray()
                    .SelectMany(item => item.TryGetProperty("foundAt", out var foundAt) && foundAt.ValueKind == JsonValueKind.Array
                        ? foundAt.EnumerateArray() : Enumerable.Empty<JsonElement>())
                    .ToList();
                spawns.AddRange(Ids(spots.Select(SpotId)));
                questSpawns.AddRange(Ids(spots
                    .Where(spot => spot.TryGetProperty("spawnedByQuests", out var quests) && quests.ValueKind == JsonValueKind.Array && quests.GetArrayLength() > 0)
                    .Select(SpotId)));
            }
            var data = new MapData(doors, [.. spawns.Distinct()], [.. questSpawns.Distinct()], stashes, devices);
            if (!data.IsEmpty)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                await File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(data), ct);
                return data;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            // Offline or the data changed shape; fall back to the last good copy.
        }

        try
        {
            var cached = File.Exists(CachePath) ? JsonSerializer.Deserialize<MapData>(await File.ReadAllTextAsync(CachePath, ct)) : null;
            // Lists missing from a cache written by an older version come back null.
            return cached is null ? Empty : new MapData(Valid(cached.DoorGuids), Valid(cached.SpawnIds), Valid(cached.QuestSpawnIds), Valid(cached.StashGuids), Valid(cached.DeviceGuids));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return Empty;
        }
    }

    private static async Task<JsonDocument> DownloadAsync(HttpClient http, string file, CancellationToken ct)
    {
        await using var stream = await http.GetStreamAsync($"{DataUrl}{file}.json", ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string? SpotId(JsonElement spot) => spot.TryGetProperty("id", out var id) ? id.GetString() : null;

    private static List<string> Ids(IEnumerable<string?> ids) => [.. ids.OfType<string>().Where(IsGuid).Distinct()];

    private static List<string> Valid(IReadOnlyList<string>? ids) => [.. (ids ?? []).Where(IsGuid)];

    private static bool IsGuid(string s) => s.Length == 32 && s.All(char.IsAsciiHexDigit);
}
