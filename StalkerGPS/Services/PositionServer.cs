using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using StalkerGPS.Game;
using StalkerGPS.Localization;
using StalkerGPS.Unreal;

namespace StalkerGPS.Services;

/// <summary>
/// Local WebSocket server that streams the tracker's status and the player's position to the
/// stalker.vodka map. Listens on loopback only and accepts a connection only when the browser's
/// Origin header is one of <see cref="AllowedOrigins"/>; everything else gets 403 Forbidden.
/// </summary>
/// <remarks>
/// Messages are JSON text frames:
/// <c>{"type":"language","code":"de"}</c> with the app's language, sent first and again if it changes,
/// <c>{"type":"status","status":"tracking"}</c>,
/// <c>{"type":"position","x":..,"y":..,"z":..,"yaw":..}</c> (Unreal units, centimeters; yaw is the
/// camera heading in degrees, 0 to 360, 0 = +X and 90 = +Y, or null if unknown),
/// <c>{"type":"doors","doors":[{"guid":..,"locked":..}, ...]}</c> with every known door,
/// <c>{"type":"spawns","spawns":[{"id":..,"collected":..}, ...]}</c> with every known world spawn,
/// <c>{"type":"stashes","stashes":[{"guid":..,"marked":..,"looted":..}, ...]}</c> with every known stash,
/// <c>{"type":"artifacts","artifacts":[{"id":..,"x":..,"y":..,"z":..}, ...]}</c> with the artifacts loaded around the player,
/// <c>{"type":"objectives","objectives":[{"id":..,"journalQuest":..}, ...],"targets":[{"x":..,"y":..,"z":..,"objectives":[..]}, ...]}</c> with the active quest markers, and
/// <c>{"type":"barricades","barricades":[{"id":..,"planks":..,"destroyed":..,"cleared":..}, ...]}</c> with every barricade on the map.
/// The current state is sent right after connecting; afterwards a message is sent when its contents
/// change. Doors, spawns, stashes, artifacts, objectives and barricades messages always hold the full list, so the latest one is all a
/// client needs. The map sends <c>mapControls</c> and <c>mapState</c> describing what it can be told
/// to do, and the app sends <c>mapCommand</c> (see <see cref="MapControlSet"/>).
/// </remarks>
public sealed class PositionServer : IDisposable
{
    /// <summary>Origins allowed to connect. Compared exactly, as browsers send them (scheme://host[:port]).</summary>
    public static readonly IReadOnlySet<string> AllowedOrigins = new HashSet<string>(StringComparer.Ordinal)
    {
        "https://stalker.vodka",
        "http://localhost:3000", // website development server
    };

    private const int MaxClients = 8;
    private const int MaxHeaderBytes = 8 * 1024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly JsonSerializerOptions CommandJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly PositionTracker _tracker;
    private readonly WorldStateTracker _world;
    private readonly ArtifactTracker _artifacts;
    private readonly QuestTracker _quests;
    private readonly BarricadeTracker _barricades;
    private readonly SettingsStore _settings;
    private readonly Localizer _localizer;
    private readonly ConcurrentDictionary<Client, byte> _clients = new();
    private readonly Lock _stateLock = new();
    private CancellationTokenSource? _cts;
    private List<TcpListener> _listeners = [];
    private string? _lastStatus;
    private string? _lastPosition;
    private readonly string?[] _lastWorld = new string?[Enum.GetValues<RecordKind>().Length];
    private string? _lastArtifacts;
    private string? _lastObjectives;
    private string? _lastBarricades;
    private int _nextCommandId;

    /// <summary>The port being listened on, or null if the server isn't running.</summary>
    public int? Port { get; private set; }

    /// <summary>Why the server couldn't start (e.g. port in use), or null.</summary>
    public Func<Strings, string>? Error { get; private set; }

    public int ClientCount => _clients.Count;

    /// <summary>The controls of the map being driven, or null if no open map offers any.</summary>
    public MapControlSet? MapControls => ControlledClient?.Controls;

    /// <summary>The overlay if it's open, otherwise the map page that connected last.</summary>
    private Client? ControlledClient => _clients.Keys
        .Where(c => c.Controls is not null)
        .OrderByDescending(c => c.Controls!.Surface == MapControlSet.OverlaySurface)
        .ThenByDescending(c => c.Id)
        .FirstOrDefault();

    /// <summary>
    /// Raised when the server starts, stops, fails, a client connects or disconnects, or a map's
    /// controls change.
    /// </summary>
    public event Action? Changed;

    public PositionServer(PositionTracker tracker, WorldStateTracker world, ArtifactTracker artifacts, QuestTracker quests, BarricadeTracker barricades, SettingsStore settings, Localizer localizer)
    {
        _tracker = tracker;
        _world = world;
        _artifacts = artifacts;
        _quests = quests;
        _barricades = barricades;
        _settings = settings;
        _localizer = localizer;
        _tracker.Changed += OnTrackerChanged;
        _world.KindChanged += OnWorldChanged;
        _artifacts.Changed += OnArtifactsChanged;
        _quests.Changed += OnQuestsChanged;
        _barricades.Changed += OnBarricadesChanged;
        _localizer.Changed += OnLanguageChanged;
    }

    public void Start()
    {
        Stop();
        int port = _settings.Current.WebSocketPort;
        _cts = new CancellationTokenSource();
        var listeners = new List<TcpListener>();
        try
        {
            // Loopback only: nothing outside this PC can connect. Browsers may resolve
            // "localhost" to either address, so listen on both.
            foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                var listener = new TcpListener(address, port);
                listener.Start();
                listeners.Add(listener);
            }
        }
        catch (SocketException ex)
        {
            listeners.ForEach(l => l.Stop());
            Error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? s => string.Format(s.ServerPortInUse, port)
                : s => string.Format(s.ServerListenFailed, port, ex.Message);
            Changed?.Invoke();
            return;
        }

        _listeners = listeners;
        Port = port;
        Error = null;
        foreach (var listener in listeners)
            _ = AcceptLoopAsync(listener, _cts.Token);
        Changed?.Invoke();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _listeners.ForEach(l => l.Stop());
        _listeners = [];
        foreach (var client in _clients.Keys)
            client.Abort();
        _clients.Clear();
        Port = null;
        Changed?.Invoke();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = HandleConnectionAsync(tcp, ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken ct)
    {
        using var connection = tcp;
        tcp.NoDelay = true;
        var stream = tcp.GetStream();

        WebSocket socket;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(HandshakeTimeout);
            try
            {
                var accepted = await HandshakeAsync(stream, timeout.Token);
                if (accepted is null)
                    return;
                socket = accepted;
            }
            catch (Exception)
            {
                // Timeout, malformed request or the peer went away.
                return;
            }
        }

        var client = new Client(socket, OnClientMessage);
        if (_clients.Count >= MaxClients || !_clients.TryAdd(client, 0))
        {
            await client.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Too many connections");
            return;
        }
        Changed?.Invoke();
        try
        {
            lock (_stateLock)
            {
                // First, so the map can label its controls before it hears anything else.
                client.Enqueue(LanguageMessage());
                client.Enqueue(_lastStatus ?? StatusMessage(_tracker.Status));
                if (_lastPosition is not null)
                    client.Enqueue(_lastPosition);
                foreach (var kind in Enum.GetValues<RecordKind>())
                {
                    if (_lastWorld[(int)kind] is { } latest)
                        client.SetLatest(kind, latest);
                }
                if (_lastArtifacts is not null)
                    client.SetLatestArtifacts(_lastArtifacts);
                if (_lastObjectives is not null)
                    client.SetLatestObjectives(_lastObjectives);
                if (_lastBarricades is not null)
                    client.SetLatestBarricades(_lastBarricades);
            }
            await client.RunAsync(ct);
        }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Abort();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Reads the HTTP upgrade request and either completes the WebSocket handshake or writes an
    /// error response. Returns null if the connection was rejected.
    /// </summary>
    private static async Task<WebSocket?> HandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var request = await ReadHeaderAsync(stream, ct);
        if (request is null)
        {
            await RespondAsync(stream, "400 Bad Request", ct);
            return null;
        }

        var lines = request.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        // Browsers always send Origin on WebSocket handshakes; anything without an allowed one is rejected.
        if (!headers.TryGetValue("Origin", out var origin) || !AllowedOrigins.Contains(origin))
        {
            await RespondAsync(stream, "403 Forbidden", ct);
            return null;
        }

        bool isUpgrade = requestLine.Length == 3 && requestLine[0] == "GET"
            && headers.TryGetValue("Upgrade", out var upgrade) && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)
            && headers.TryGetValue("Connection", out var connection) && connection.Contains("upgrade", StringComparison.OrdinalIgnoreCase)
            && headers.TryGetValue("Sec-WebSocket-Version", out var version) && version == "13"
            && headers.TryGetValue("Sec-WebSocket-Key", out var key) && IsValidKey(key);
        if (!isUpgrade)
        {
            await RespondAsync(stream, "400 Bad Request", ct);
            return null;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"] + WebSocketGuid)));
        var response = "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);

        return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });
    }

    private static bool IsValidKey(string key)
    {
        Span<byte> decoded = stackalloc byte[24];
        return Convert.TryFromBase64String(key, decoded, out int length) && length == 16;
    }

    /// <summary>Reads up to the blank line ending the HTTP headers, or returns null if it's too long.</summary>
    private static async Task<string?> ReadHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        int length = 0;
        while (length < buffer.Length)
        {
            // One byte at a time so nothing after the headers is consumed; headers are small.
            int read = await stream.ReadAsync(buffer.AsMemory(length, 1), ct);
            if (read == 0)
                return null;
            length++;
            if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n' && buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
                return Encoding.ASCII.GetString(buffer, 0, length - 4);
        }
        return null;
    }

    private static Task RespondAsync(NetworkStream stream, string status, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct).AsTask();

    private void OnTrackerChanged()
    {
        // The tracker raises Changed on every poll; only send what actually changed.
        string status = StatusMessage(_tracker.Status);
        string? position = _tracker.Position is { } p
            ? JsonSerializer.Serialize(new PositionMessage("position", p.X, p.Y, p.Z, p.Yaw), JsonOptions)
            : null;

        var outgoing = new List<string>(2);
        lock (_stateLock)
        {
            if (status != _lastStatus)
            {
                _lastStatus = status;
                outgoing.Add(status);
            }
            if (position is not null && position != _lastPosition)
                outgoing.Add(position);
            _lastPosition = position;
        }

        foreach (var message in outgoing)
            foreach (var client in _clients.Keys)
                client.Enqueue(message);
    }

    private void OnWorldChanged(RecordKind kind)
    {
        var message = kind switch
        {
            RecordKind.Door => JsonSerializer.Serialize(new DoorsMessage("doors", [.. _world.Doors.Select(kv => new DoorEntry(kv.Key, kv.Value.HasFlag(ObjectState.Locked)))]), JsonOptions),
            RecordKind.Spawn => JsonSerializer.Serialize(new SpawnsMessage("spawns", [.. _world.Spawns.Select(kv => new SpawnEntry(kv.Key, kv.Value.HasFlag(ObjectState.Collected)))]), JsonOptions),
            _ => JsonSerializer.Serialize(new StashesMessage("stashes", [.. _world.Stashes.Select(kv => new StashEntry(kv.Key, kv.Value.HasFlag(ObjectState.Marked), kv.Value.HasFlag(ObjectState.Looted)))]), JsonOptions),
        };
        lock (_stateLock)
            _lastWorld[(int)kind] = message;
        foreach (var client in _clients.Keys)
            client.SetLatest(kind, message);
    }

    private void OnArtifactsChanged()
    {
        var message = JsonSerializer.Serialize(new ArtifactsMessage("artifacts", _artifacts.Artifacts), JsonOptions);
        lock (_stateLock)
            _lastArtifacts = message;
        foreach (var client in _clients.Keys)
            client.SetLatestArtifacts(message);
    }

    private void OnQuestsChanged()
    {
        var message = JsonSerializer.Serialize(new ObjectivesMessage("objectives", _quests.Objectives, _quests.Targets), JsonOptions);
        lock (_stateLock)
            _lastObjectives = message;
        foreach (var client in _clients.Keys)
            client.SetLatestObjectives(message);
    }

    private void OnBarricadesChanged()
    {
        var message = JsonSerializer.Serialize(new BarricadesMessage("barricades", _barricades.Barricades), JsonOptions);
        lock (_stateLock)
            _lastBarricades = message;
        foreach (var client in _clients.Keys)
            client.SetLatestBarricades(message);
    }

    /// <summary>Tells the map to set a control to <paramref name="value"/> (or none, for an action) or move it by <paramref name="delta"/>.</summary>
    public void SendMapCommand(string control, object? value = null, double? delta = null)
    {
        int id = Interlocked.Increment(ref _nextCommandId);
        ControlledClient?.Enqueue(JsonSerializer.Serialize(new MapCommandBody("mapCommand", id, control, value, delta), CommandJsonOptions));
    }

    private void OnClientMessage(Client client, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var message = document.RootElement;
            switch (message.GetProperty("type").GetString())
            {
                case "mapControls":
                    client.Controls = MapControlSet.Parse(message) is { } list ? list with { Connection = client.Id } : null;
                    Changed?.Invoke();
                    break;
                case "mapState" when client.Controls is { } controls:
                    client.Controls = controls.WithState(message);
                    Changed?.Invoke();
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // Not something this app understands; a newer map may send more than it knows.
        }
    }

    private void OnLanguageChanged()
    {
        string message = LanguageMessage();
        foreach (var client in _clients.Keys)
            client.SetLatestLanguage(message);
    }

    private string LanguageMessage() =>
        JsonSerializer.Serialize(new LanguageMessageBody("language", _localizer.Code), JsonOptions);

    private static string StatusMessage(TrackerStatus status) =>
        JsonSerializer.Serialize(new StatusMessageBody("status", status switch
        {
            TrackerStatus.Attached => "tracking",
            TrackerStatus.WaitingForGame => "waitingForGame",
            TrackerStatus.Locating => "locating",
            TrackerStatus.PointerInvalid => "noPlayer",
            TrackerStatus.Error => "error",
            _ => "stopped",
        }), JsonOptions);

    public void Dispose()
    {
        _tracker.Changed -= OnTrackerChanged;
        _world.KindChanged -= OnWorldChanged;
        _artifacts.Changed -= OnArtifactsChanged;
        _quests.Changed -= OnQuestsChanged;
        _barricades.Changed -= OnBarricadesChanged;
        _localizer.Changed -= OnLanguageChanged;
        Stop();
    }

    private sealed record LanguageMessageBody(string Type, string Code);

    private sealed record MapCommandBody(string Type, int Id, string Control, object? Value, double? Delta);

    private sealed record StatusMessageBody(string Type, string Status);

    private sealed record PositionMessage(string Type, double X, double Y, double Z, double? Yaw);

    private sealed record DoorsMessage(string Type, IReadOnlyList<DoorEntry> Doors);

    private sealed record DoorEntry(string Guid, bool Locked);

    private sealed record SpawnsMessage(string Type, IReadOnlyList<SpawnEntry> Spawns);

    private sealed record SpawnEntry(string Id, bool Collected);

    private sealed record StashesMessage(string Type, IReadOnlyList<StashEntry> Stashes);

    private sealed record StashEntry(string Guid, bool Marked, bool Looted);

    private sealed record ArtifactsMessage(string Type, IReadOnlyList<LiveArtifact> Artifacts);

    private sealed record ObjectivesMessage(string Type, IReadOnlyList<LiveObjective> Objectives, IReadOnlyList<QuestTarget> Targets);

    private sealed record BarricadesMessage(string Type, IReadOnlyList<BarricadeState> Barricades);

    /// <summary>One connected browser. Sends are queued so a slow client never blocks the tracker.</summary>
    private sealed class Client(WebSocket socket, Action<Client, string> onMessage)
    {
        private const int MaxIncomingBytes = 16 * 1024;

        // Small bounded queue: if a client falls behind, the oldest updates are dropped.
        private readonly Channel<string> _outgoing = Channel.CreateBounded<string>(
            new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        // The latest doors, spawns, stashes, language, artifacts, objectives and barricades messages wait here instead of in the queue,
        // where they could be dropped; only the newest of each kind matters.
        private static readonly int LanguageSlot = Enum.GetValues<RecordKind>().Length;
        private static readonly int ArtifactsSlot = LanguageSlot + 1;
        private static readonly int ObjectivesSlot = ArtifactsSlot + 1;
        private static readonly int BarricadesSlot = ObjectivesSlot + 1;
        private readonly string?[] _pending = new string?[BarricadesSlot + 1];
        private static readonly string PendingSignal = new("pending".AsSpan());

        private static long _lastId;

        public long Id { get; } = Interlocked.Increment(ref _lastId);

        public MapControlSet? Controls { get; set; }

        public void Enqueue(string message) => _outgoing.Writer.TryWrite(message);

        public void SetLatest(RecordKind kind, string message) => SetPending((int)kind, message);

        public void SetLatestLanguage(string message) => SetPending(LanguageSlot, message);

        public void SetLatestArtifacts(string message) => SetPending(ArtifactsSlot, message);

        public void SetLatestObjectives(string message) => SetPending(ObjectivesSlot, message);

        public void SetLatestBarricades(string message) => SetPending(BarricadesSlot, message);

        private void SetPending(int slot, string message)
        {
            Volatile.Write(ref _pending[slot], message);
            _outgoing.Writer.TryWrite(PendingSignal);
        }

        public async Task RunAsync(CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var receive = ReceiveAsync(linked.Token);
            try
            {
                await foreach (var queued in _outgoing.Reader.ReadAllAsync(linked.Token))
                {
                    if (socket.State != WebSocketState.Open)
                        break;
                    if (!ReferenceEquals(queued, PendingSignal))
                        await socket.SendAsync(Encoding.UTF8.GetBytes(queued), WebSocketMessageType.Text, true, linked.Token);
                    // Checked after every message, so these still go out if their signal was dropped.
                    for (int i = 0; i < _pending.Length; i++)
                    {
                        if (Interlocked.Exchange(ref _pending[i], null) is { } latest)
                            await socket.SendAsync(Encoding.UTF8.GetBytes(latest), WebSocketMessageType.Text, true, linked.Token);
                    }
                }
            }
            catch (Exception) when (linked.IsCancellationRequested || socket.State != WebSocketState.Open)
            {
            }
            catch (WebSocketException)
            {
            }
            finally
            {
                await linked.CancelAsync();
                try { await receive; } catch { }
            }
        }

        /// <summary>Handles the map's messages, skipping any too long to be one, and close and keep-alive frames.</summary>
        private async Task ReceiveAsync(CancellationToken ct)
        {
            var buffer = new byte[MaxIncomingBytes];
            int length = 0;
            bool skipping = false;
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer.AsMemory(length), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                        break;
                    }

                    length += result.Count;
                    if (result.EndOfMessage)
                    {
                        if (!skipping && result.MessageType == WebSocketMessageType.Text)
                            onMessage(this, Encoding.UTF8.GetString(buffer, 0, length));
                        length = 0;
                        skipping = false;
                    }
                    else if (length == buffer.Length)
                    {
                        length = 0;
                        skipping = true;
                    }
                }
            }
            finally
            {
                // Stop the send loop too.
                _outgoing.Writer.TryComplete();
            }
        }

        public async Task CloseAsync(WebSocketCloseStatus status, string description)
        {
            try { await socket.CloseOutputAsync(status, description, CancellationToken.None); } catch { }
            Abort();
        }

        public void Abort()
        {
            _outgoing.Writer.TryComplete();
            socket.Abort();
            socket.Dispose();
        }
    }
}
