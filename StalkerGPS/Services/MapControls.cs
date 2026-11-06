using System.Text.Json;

namespace StalkerGPS.Services;

public enum MapControlKind
{
    Toggle,
    Range,
    Action,
}

/// <summary>A control the map offers. <see cref="Id"/> is stable; <see cref="Label"/> is already translated.</summary>
public sealed record MapControl(string Id, MapControlKind Kind, string Label, bool Enabled)
{
    public bool? IsOn { get; init; }
    public double? Number { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; }
    public string? Group { get; init; }
    public string? Parent { get; init; }
}

/// <summary>
/// What one connected map page can be told to do, from its <c>mapControls</c> and <c>mapState</c>
/// messages. The page owns the list, so new controls appear here without an app update.
/// </summary>
public sealed record MapControlSet(string Surface, IReadOnlyList<MapControl> Controls)
{
    private const int SupportedVersion = 1;

    /// <summary>Identifies the connection, which is a new one each time a page loads.</summary>
    public long Connection { get; init; }

    public const string OverlaySurface = "overlay";

    public MapControl? Find(string id) => Controls.FirstOrDefault(c => c.Id == id);

    /// <summary>Reads a <c>mapControls</c> message. Returns null for a protocol version this app doesn't know.</summary>
    public static MapControlSet? Parse(JsonElement message)
    {
        if (message.GetProperty("version").GetInt32() != SupportedVersion)
            return null;
        var controls = message.GetProperty("controls").EnumerateArray().Select(ParseControl).OfType<MapControl>().ToList();
        return new MapControlSet(message.GetProperty("surface").GetString() ?? "page", controls);
    }

    /// <summary>Applies a <c>mapState</c> message, which holds only what changed.</summary>
    public MapControlSet WithState(JsonElement message)
    {
        var values = message.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
        var enabled = message.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
        return this with
        {
            Controls = Controls.Select(control =>
            {
                if (values.ValueKind == JsonValueKind.Object && values.TryGetProperty(control.Id, out var value))
                    control = WithValue(control, value);
                if (enabled.ValueKind == JsonValueKind.Object && enabled.TryGetProperty(control.Id, out var flag)
                    && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    control = control with { Enabled = flag.GetBoolean() };
                return control;
            }).ToList(),
        };
    }

    private static MapControl? ParseControl(JsonElement item)
    {
        MapControlKind? kind = item.GetProperty("kind").GetString() switch
        {
            "toggle" => MapControlKind.Toggle,
            "range" => MapControlKind.Range,
            "action" => MapControlKind.Action,
            _ => null,
        };
        if (kind is null)
            return null;

        var control = new MapControl(
            item.GetProperty("id").GetString()!,
            kind.Value,
            item.GetProperty("label").GetString()!,
            item.GetProperty("enabled").GetBoolean())
        {
            Group = item.TryGetProperty("group", out var group) ? group.GetString() : null,
            Parent = item.TryGetProperty("parent", out var parent) ? parent.GetString() : null,
        };
        if (kind == MapControlKind.Range)
        {
            control = control with
            {
                Min = item.GetProperty("min").GetDouble(),
                Max = item.GetProperty("max").GetDouble(),
                Step = item.GetProperty("step").GetDouble(),
            };
        }
        return item.TryGetProperty("value", out var value) ? WithValue(control, value) : control;
    }

    private static MapControl WithValue(MapControl control, JsonElement value) => control.Kind switch
    {
        MapControlKind.Toggle when value.ValueKind is JsonValueKind.True or JsonValueKind.False => control with { IsOn = value.GetBoolean() },
        MapControlKind.Range when value.ValueKind == JsonValueKind.Number => control with { Number = value.GetDouble() },
        _ => control,
    };
}
