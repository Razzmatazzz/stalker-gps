using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace StalkerGPS.Services;

/// <summary>
/// A boarded-up opening on the map: the ID the map knows it by, its planks' GUIDs, and whether any of
/// them only appear through a quest.
/// </summary>
public sealed record Barricade(string Id, IReadOnlyList<string> Planks, bool QuestPlanks);

/// <summary>The barricades on the stalker.vodka map. Downloaded from the site and cached so it also works offline.</summary>
public static class BarricadeData
{
    // map.json holds the locks the map shows; locks.json is no longer updated but is still read if map.json lacks them.
    private const string DataUrl = "https://assets.stalker.vodka/data/map.json";
    private const string LocksUrl = "https://assets.stalker.vodka/data/locks.json";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StalkerGPS", "barricades.json");

    /// <summary>Downloads the barricades, or returns the cached copy if the site can't be reached.</summary>
    public static async Task<IReadOnlyList<Barricade>> LoadAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = DownloadTimeout };
            List<Barricade> barricades;
            await using (var stream = await http.GetStreamAsync(DataUrl, ct))
            using (var map = await JsonDocument.ParseAsync(stream, cancellationToken: ct))
            {
                barricades = map.RootElement.ValueKind == JsonValueKind.Object && map.RootElement.TryGetProperty("locks", out var locks)
                    && locks.ValueKind == JsonValueKind.Array ? Parse(locks) : [];
            }
            if (barricades.Count == 0)
            {
                await using var stream = await http.GetStreamAsync(LocksUrl, ct);
                using var locks = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                barricades = Parse(locks.RootElement);
            }
            if (barricades.Count > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                await File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(barricades), ct);
                return barricades;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            // Offline or the data changed shape; fall back to the last good copy.
        }

        try
        {
            return File.Exists(CachePath)
                ? JsonSerializer.Deserialize<List<Barricade>>(await File.ReadAllTextAsync(CachePath, ct)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private static List<Barricade> Parse(JsonElement locks)
    {
        var barricades = new Dictionary<string, Barricade>();
        foreach (var entry in locks.EnumerateArray())
        {
            if (!entry.TryGetProperty("openedBy", out var openers) || openers.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var opener in openers.EnumerateArray())
            {
                // Other "barricade" openers are objects in the world, with no planks to track.
                if (opener.TryGetProperty("barricade", out var id) && id.GetString() is { } barricade
                    && opener.TryGetProperty("planks", out var planks) && planks.ValueKind == JsonValueKind.Array)
                {
                    barricades[barricade] = new Barricade(barricade, [.. planks.EnumerateArray().Select(p => p.GetString()!)],
                        opener.TryGetProperty("questPlanks", out var quest) && quest.ValueKind == JsonValueKind.True);
                }
            }
        }
        return [.. barricades.Values];
    }
}
