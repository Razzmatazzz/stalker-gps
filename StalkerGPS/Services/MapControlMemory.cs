namespace StalkerGPS.Services;

/// <summary>
/// Remembers the overlay's map controls and puts them back whenever it connects. The map reports
/// every change, whatever caused it, so this also covers the hotkeys and using the map directly.
/// Ordinary browser tabs are left alone: they aren't the app's to change.
/// </summary>
public sealed class MapControlMemory : IDisposable
{
    private readonly PositionServer _server;
    private readonly SettingsStore _settings;
    private readonly Lock _lock = new();
    private long _connection;

    // Saved values not yet sent, because the control has been disabled so far (a lock group while
    // locks are hidden, say).
    private Dictionary<string, object> _toRestore = [];

    // Each control's last known value. Only a change is saved, so the map's starting values aren't.
    private Dictionary<string, object> _seen = [];

    public MapControlMemory(PositionServer server, SettingsStore settings)
    {
        _server = server;
        _settings = settings;
        _server.Changed += OnServerChanged;
    }

    private void OnServerChanged()
    {
        if (_server.MapControls is not { Surface: MapControlSet.OverlaySurface } controls)
            return;

        lock (_lock)
        {
            if (controls.Connection != _connection)
            {
                _connection = controls.Connection;
                _toRestore = _settings.MapControlValues();
                _seen = [];
            }

            // The map zooms in when the player's position appears, so the zoom has to wait for that.
            bool playerKnown = controls.Find("followPlayer") is not { Enabled: false };

            foreach (var control in controls.Controls)
            {
                object? current = (object?)control.IsOn ?? control.Number;
                if (current is not { } value)
                    continue;

                if (_toRestore.TryGetValue(control.Id, out var saved))
                {
                    if (control.Enabled && (control.Id != "zoom" || playerKnown))
                    {
                        _toRestore.Remove(control.Id);
                        if (!saved.Equals(value))
                            _server.SendMapCommand(control.Id, saved);
                    }
                }
                else if (_seen.TryGetValue(control.Id, out var last) && !last.Equals(value))
                {
                    _settings.RememberMapControl(control.Id, value);
                }
                _seen[control.Id] = value;
            }
        }
    }

    public void Dispose() => _server.Changed -= OnServerChanged;
}
