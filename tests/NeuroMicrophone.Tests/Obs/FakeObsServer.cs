using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroMicrophone.Tests.Obs;

/// <summary>
/// Поддельный сервер obs-websocket v5 для тестов клиента: слушает на
/// loopback-порту, делает настоящее WebSocket-рукопожатие (без http.sys —
/// поэтому не нужны права администратора и он одинаково работает на Windows
/// и Linux) и ведёт себя строго по документации протокола — Hello →
/// Identify (с SHA-256-авторизацией) → Identified, затем Request →
/// RequestResponse, коды ошибок 600/601/607, закрытие с кодом 4009 при
/// неверном пароле.
///
/// Сервер хранит маленькую модель OBS (источники и их фильтры), поэтому
/// тесты проверяют не "что клиент отправил", а итоговое СОСТОЯНИЕ OBS после
/// применения настроек.
/// </summary>
public sealed class FakeObsServer : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const string SubProtocol = "obswebsocket.json";

    private static readonly HashSet<string> KnownFilterKinds = new()
    {
        "noise_suppress_filter_v2", "noise_gate_filter", "compressor_filter", "limiter_filter", "gain_filter",
    };

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly object _gate = new();
    private readonly List<string> _requestLog = new();

    public int Port { get; }

    /// <summary>null — авторизация не требуется (как в OBS с выключенным паролем).</summary>
    public string? Password { get; }

    /// <summary>Отправлять каждый ответ двумя кадрами WebSocket (проверка сборки сообщения из частей).</summary>
    public bool SplitResponsesIntoFrames { get; set; }

    /// <summary>Перед каждым ответом присылать постороннее событие OBS (клиент должен его пропустить).</summary>
    public bool SendEventBeforeEachResponse { get; set; }

    public List<FakeSource> Sources { get; } = new();

    public FakeObsServer(string? password = null)
    {
        Password = password;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Типы запросов в порядке получения (для проверки, что лишнего клиент не отправлял).</summary>
    public IReadOnlyList<string> RequestLog
    {
        get
        {
            lock (_gate) return _requestLog.ToList();
        }
    }

    public FakeSource AddSource(string name, string kind, int caps)
    {
        var source = new FakeSource(name, kind, caps);
        lock (_gate) Sources.Add(source);
        return source;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (Exception) { /* уже остановлен */ }

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Завершение цикла приёма из-за остановки listener — штатно.
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Listener остановлен (DisposeAsync) или токен отменён — цикл приёма просто завершается.
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient tcp)
    {
        using (tcp)
        {
            try
            {
                NetworkStream stream = tcp.GetStream();
                await PerformHandshakeAsync(stream, _cts.Token).ConfigureAwait(false);

                using WebSocket socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
                {
                    IsServer = true,
                    SubProtocol = SubProtocol,
                    KeepAliveInterval = TimeSpan.Zero,
                });

                await ServeAsync(socket, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Клиент оборвал соединение или сервер останавливается — для теста это не ошибка.
            }
        }
    }

    private static async Task PerformHandshakeAsync(NetworkStream stream, CancellationToken token)
    {
        var received = new List<byte>();
        var chunk = new byte[1024];
        string request;

        while (true)
        {
            int read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0) throw new InvalidOperationException("Клиент закрыл соединение до конца HTTP-заголовков.");

            received.AddRange(chunk.Take(read));
            request = Encoding.ASCII.GetString(received.ToArray());
            if (request.Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }

        string? key = null;
        foreach (string line in request.Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            if (line.AsSpan(0, colon).Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            {
                key = line[(colon + 1)..].Trim();
            }
        }

        if (key == null) throw new InvalidOperationException("В запросе нет Sec-WebSocket-Key.");

        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        string response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            $"Sec-WebSocket-Protocol: {SubProtocol}\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token).ConfigureAwait(false);
    }

    private async Task ServeAsync(WebSocket socket, CancellationToken token)
    {
        var helloData = new JsonObject
        {
            ["obsStudioVersion"] = "31.0.0",
            ["obsWebSocketVersion"] = "5.5.4",
            ["rpcVersion"] = 1,
        };

        string? salt = null;
        string? challenge = null;
        if (Password != null)
        {
            salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            helloData["authentication"] = new JsonObject { ["challenge"] = challenge, ["salt"] = salt };
        }

        await SendAsync(socket, new JsonObject { ["op"] = 0, ["d"] = helloData }, split: false, token).ConfigureAwait(false);

        JsonObject? identify = await ReceiveAsync(socket, token).ConfigureAwait(false);
        if (identify == null || (int?)identify["op"] != 1) return;

        if (Password != null)
        {
            string? provided = (string?)(identify["d"] as JsonObject)?["authentication"];
            string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Password + salt)));
            string expected = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));

            if (provided != expected)
            {
                // WebSocketCloseCode::AuthenticationFailed = 4009 по документации obs-websocket.
                await socket.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed.", token).ConfigureAwait(false);
                return;
            }
        }

        await SendAsync(socket, new JsonObject { ["op"] = 2, ["d"] = new JsonObject { ["negotiatedRpcVersion"] = 1 } }, split: false, token)
            .ConfigureAwait(false);

        while (!token.IsCancellationRequested)
        {
            JsonObject? message = await ReceiveAsync(socket, token).ConfigureAwait(false);
            if (message == null) return;
            if ((int?)message["op"] != 6 || message["d"] is not JsonObject request) continue;

            string requestType = (string)request["requestType"]!;
            string requestId = (string)request["requestId"]!;
            JsonObject data = request["requestData"] as JsonObject ?? new JsonObject();

            lock (_gate) _requestLog.Add(requestType);

            (int code, string? comment, JsonObject? responseData) = Handle(requestType, data);

            if (SendEventBeforeEachResponse)
            {
                await SendAsync(socket, new JsonObject
                {
                    ["op"] = 5,
                    ["d"] = new JsonObject
                    {
                        ["eventType"] = "InputVolumeChanged",
                        ["eventIntent"] = 8,
                        ["eventData"] = new JsonObject { ["inputName"] = "x", ["inputVolumeDb"] = -3.5 },
                    },
                }, split: false, token).ConfigureAwait(false);
            }

            var status = new JsonObject { ["result"] = code == 100, ["code"] = code };
            if (comment != null) status["comment"] = comment;

            var responseBody = new JsonObject
            {
                ["requestType"] = requestType,
                ["requestId"] = requestId,
                ["requestStatus"] = status,
            };
            if (responseData != null) responseBody["responseData"] = responseData;

            await SendAsync(socket, new JsonObject { ["op"] = 7, ["d"] = responseBody }, SplitResponsesIntoFrames, token).ConfigureAwait(false);
        }
    }

    private (int Code, string? Comment, JsonObject? Data) Handle(string requestType, JsonObject data)
    {
        lock (_gate)
        {
            if (requestType == "GetInputList")
            {
                var inputs = new JsonArray();
                foreach (FakeSource source in Sources)
                {
                    inputs.Add(new JsonObject
                    {
                        ["inputName"] = source.Name,
                        ["inputUuid"] = Guid.NewGuid().ToString(),
                        ["inputKind"] = source.Kind,
                        ["unversionedInputKind"] = source.Kind,
                        ["inputKindCaps"] = source.Caps,
                    });
                }

                return (100, null, new JsonObject { ["inputs"] = inputs });
            }

            string? sourceName = (string?)data["sourceName"];
            FakeSource? target = Sources.FirstOrDefault(s => s.Name == sourceName);
            if (target == null)
            {
                return (600, $"No source was found by the name of `{sourceName}` within the canvas `Main`.", null);
            }

            string? filterName = (string?)data["filterName"];
            FakeFilter? filter = target.Filters.FirstOrDefault(f => f.Name == filterName);

            switch (requestType)
            {
                case "GetSourceFilterList":
                {
                    var filters = new JsonArray();
                    for (int i = 0; i < target.Filters.Count; i++)
                    {
                        FakeFilter f = target.Filters[i];
                        filters.Add(new JsonObject
                        {
                            ["filterEnabled"] = f.Enabled,
                            ["filterIndex"] = i,
                            ["filterKind"] = f.Kind,
                            ["filterName"] = f.Name,
                            ["filterSettings"] = JsonNode.Parse(f.Settings.ToJsonString()),
                        });
                    }

                    return (100, null, new JsonObject { ["filters"] = filters });
                }

                case "CreateSourceFilter":
                {
                    if (filter != null) return (601, "A filter already exists by that name.", null);

                    string kind = (string)data["filterKind"]!;
                    if (!KnownFilterKinds.Contains(kind)) return (607, "Invalid filter kind.", null);

                    var created = new FakeFilter(filterName!, kind) { Enabled = true };
                    if (data["filterSettings"] is JsonObject settings)
                    {
                        foreach (KeyValuePair<string, JsonNode?> pair in settings)
                        {
                            created.Settings[pair.Key] = JsonNode.Parse(pair.Value!.ToJsonString());
                        }
                    }

                    target.Filters.Add(created);
                    return (100, null, null);
                }

                case "SetSourceFilterSettings":
                {
                    if (filter == null) return (600, "No filter was found by that name.", null);

                    bool overlay = data["overlay"] is not JsonValue overlayValue || overlayValue.GetValue<bool>();
                    if (!overlay) filter.Settings.Clear();

                    foreach (KeyValuePair<string, JsonNode?> pair in (JsonObject)data["filterSettings"]!)
                    {
                        filter.Settings[pair.Key] = JsonNode.Parse(pair.Value!.ToJsonString());
                    }

                    return (100, null, null);
                }

                case "SetSourceFilterEnabled":
                    if (filter == null) return (600, "No filter was found by that name.", null);
                    filter.Enabled = (bool)data["filterEnabled"]!;
                    return (100, null, null);

                case "RemoveSourceFilter":
                    if (filter == null) return (600, "No filter was found by that name.", null);
                    target.Filters.Remove(filter);
                    return (100, null, null);

                default:
                    return (204, $"Unknown request type {requestType}.", null);
            }
        }
    }

    private static async Task SendAsync(WebSocket socket, JsonObject message, bool split, CancellationToken token)
    {
        byte[] payload = Encoding.UTF8.GetBytes(message.ToJsonString());

        if (!split || payload.Length < 2)
        {
            await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, token).ConfigureAwait(false);
            return;
        }

        int middle = payload.Length / 2;
        await socket.SendAsync(payload.AsMemory(0, middle), WebSocketMessageType.Text, endOfMessage: false, token).ConfigureAwait(false);
        await socket.SendAsync(payload.AsMemory(middle), WebSocketMessageType.Text, endOfMessage: true, token).ConfigureAwait(false);
    }

    private static async Task<JsonObject?> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var message = new System.IO.MemoryStream();

        while (true)
        {
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
                }

                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        return JsonNode.Parse(message.ToArray()) as JsonObject;
    }
}

public sealed class FakeSource
{
    public string Name { get; }
    public string Kind { get; }
    public int Caps { get; }
    public List<FakeFilter> Filters { get; } = new();

    public FakeSource(string name, string kind, int caps)
    {
        Name = name;
        Kind = kind;
        Caps = caps;
    }

    public FakeFilter? Filter(string name) => Filters.FirstOrDefault(f => f.Name == name);
}

public sealed class FakeFilter
{
    public string Name { get; }
    public string Kind { get; }
    public bool Enabled { get; set; }
    public JsonObject Settings { get; } = new();

    public FakeFilter(string name, string kind)
    {
        Name = name;
        Kind = kind;
    }

    public double Number(string key) => Settings[key]!.GetValue<double>();
}
