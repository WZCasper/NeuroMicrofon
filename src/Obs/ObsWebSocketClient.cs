using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroMicrophone.Obs;

/// <summary>
/// Минимальный, но настоящий клиент протокола obs-websocket v5 (встроен в OBS
/// Studio начиная с версии 28): WebSocket + JSON, рукопожатие Hello →
/// Identify → Identified с SHA-256-авторизацией и запросы Request →
/// RequestResponse. Работает только на стандартных средствах .NET
/// (ClientWebSocket + System.Text.Json) — дополнительных NuGet-пакетов не
/// требует.
///
/// Соединение короткоживущее: открыли → выполнили нужные запросы → закрыли.
/// Это сознательно проще и надёжнее постоянного соединения: не нужно
/// переподключаться после перезапуска OBS и следить за "зависшими" сокетами,
/// а события OBS нам не нужны вовсе (eventSubscriptions = 0).
///
/// Формат сообщений, коды операций и алгоритм авторизации сверены с
/// официальной документацией протокола:
/// https://github.com/obsproject/obs-websocket/blob/master/docs/generated/protocol.md
/// </summary>
public sealed class ObsWebSocketClient : IAsyncDisposable
{
    private const int RpcVersion = 1;
    private const int OpHello = 0;
    private const int OpIdentify = 1;
    private const int OpIdentified = 2;
    private const int OpRequest = 6;
    private const int OpRequestResponse = 7;

    // WebSocketCloseCode::AuthenticationFailed из протокола obs-websocket.
    private const int CloseCodeAuthenticationFailed = 4009;

    private const int MaxMessageBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly ClientWebSocket _socket = new();
    private readonly byte[] _receiveBuffer = new byte[16 * 1024];
    private int _requestCounter;

    /// <summary>Версия OBS Studio из приветствия сервера (например, "31.0.0").</summary>
    public string ObsStudioVersion { get; private set; } = "";

    /// <summary>Версия плагина obs-websocket из приветствия сервера (например, "5.5.4").</summary>
    public string ObsWebSocketVersion { get; private set; } = "";

    private ObsWebSocketClient()
    {
    }

    /// <summary>
    /// Подключается к OBS и проходит рукопожатие (в том числе авторизацию).
    /// При любой ошибке соединение закрывается, а наружу уходит ObsException
    /// с понятным пользователю текстом.
    /// </summary>
    public static async Task<ObsWebSocketClient> ConnectAsync(ObsConnectionSettings settings, CancellationToken cancellationToken)
    {
        var client = new ObsWebSocketClient();
        try
        {
            await client.HandshakeAsync(settings, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Строка авторизации для Identify по алгоритму из документации
    /// obs-websocket: base64(SHA256(base64(SHA256(пароль + salt)) + challenge)).
    /// </summary>
    public static string ComputeAuthentication(string password, string salt, string challenge)
    {
        byte[] secretHash = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        string secret = Convert.ToBase64String(secretHash);

        byte[] authHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge));
        return Convert.ToBase64String(authHash);
    }

    /// <summary>
    /// Отправляет запрос и ждёт ответа на него. Возвращает responseData
    /// (пустой объект, если OBS его не прислал). Если OBS отклонил запрос,
    /// бросает ObsRequestException с кодом и комментарием OBS.
    /// </summary>
    public async Task<JsonObject> SendRequestAsync(string requestType, JsonObject? requestData, CancellationToken cancellationToken)
    {
        string requestId = "nm-" + Interlocked.Increment(ref _requestCounter).ToString(CultureInfo.InvariantCulture);

        var data = new JsonObject
        {
            ["requestType"] = requestType,
            ["requestId"] = requestId,
        };
        if (requestData != null)
        {
            data["requestData"] = requestData;
        }

        await SendAsync(new JsonObject { ["op"] = OpRequest, ["d"] = data }, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            JsonObject message = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);

            // Всё, кроме ответа на наш запрос (например, события OBS), пропускаем.
            if (ReadInt(message["op"]) != OpRequestResponse) continue;
            if (message["d"] is not JsonObject response) continue;
            if (ReadString(response["requestId"]) != requestId) continue;

            JsonObject? status = response["requestStatus"] as JsonObject;
            bool succeeded = status?["result"] is JsonValue resultValue
                             && resultValue.TryGetValue(out bool resultFlag)
                             && resultFlag;

            if (!succeeded)
            {
                int code = ReadInt(status?["code"]) ?? 0;
                throw new ObsRequestException(requestType, code, ReadString(status?["comment"]));
            }

            return response["responseData"] as JsonObject ?? new JsonObject();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", closeTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Соединение могло быть уже разорвано — при закрытии это не критично.
        }
        finally
        {
            _socket.Dispose();
        }
    }

    private async Task HandshakeAsync(ObsConnectionSettings settings, CancellationToken cancellationToken)
    {
        Uri uri = BuildUri(settings);
        _socket.Options.AddSubProtocol("obswebsocket.json");

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectCts.CancelAfter(ConnectTimeout);
            try
            {
                await _socket.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ObsException(
                    $"OBS не ответил по адресу {settings.Host}:{settings.Port} за {ConnectTimeout.TotalSeconds:F0} с. " +
                    "Проверьте адрес и порт сервера WebSocket в OBS.");
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or SocketException)
            {
                throw new ObsException(
                    $"Не удалось подключиться к OBS по адресу {settings.Host}:{settings.Port}. " +
                    "Убедитесь, что OBS запущен, а сервер WebSocket включён " +
                    "(OBS → Сервис → Настройки сервера WebSocket → «Включить сервер WebSocket»).",
                    ex);
            }
        }

        // 1) Hello от сервера. Если в нём есть блок authentication — нужен пароль.
        JsonObject hello = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
        if (ReadInt(hello["op"]) != OpHello || hello["d"] is not JsonObject helloData)
        {
            throw new ObsException(
                $"По адресу {settings.Host}:{settings.Port} отвечает не OBS WebSocket (не пришло приветствие Hello). " +
                "Проверьте порт — по умолчанию в OBS это 4455.");
        }

        ObsStudioVersion = ReadString(helloData["obsStudioVersion"]) ?? "";
        ObsWebSocketVersion = ReadString(helloData["obsWebSocketVersion"]) ?? "";

        // 2) Identify — без подписки на события (нам нужны только запросы).
        var identifyData = new JsonObject
        {
            ["rpcVersion"] = RpcVersion,
            ["eventSubscriptions"] = 0,
        };

        if (helloData["authentication"] is JsonObject authentication)
        {
            if (string.IsNullOrEmpty(settings.Password))
            {
                throw new ObsException(
                    "OBS требует пароль. Введите пароль сервера WebSocket " +
                    "(OBS → Сервис → Настройки сервера WebSocket → «Показать информацию о подключении»).");
            }

            string? challenge = ReadString(authentication["challenge"]);
            string? salt = ReadString(authentication["salt"]);
            if (challenge == null || salt == null)
            {
                throw new ObsException("OBS прислал некорректный запрос авторизации (нет challenge/salt).");
            }

            identifyData["authentication"] = ComputeAuthentication(settings.Password, salt, challenge);
        }

        await SendAsync(new JsonObject { ["op"] = OpIdentify, ["d"] = identifyData }, cancellationToken).ConfigureAwait(false);

        // 3) Identified. При неверном пароле OBS закрывает соединение с кодом 4009.
        JsonObject identified;
        try
        {
            identified = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObsConnectionClosedException ex) when (ex.CloseCode == CloseCodeAuthenticationFailed)
        {
            throw new ObsException("Неверный пароль OBS WebSocket. Проверьте пароль в настройках сервера OBS.", ex);
        }

        if (ReadInt(identified["op"]) != OpIdentified)
        {
            throw new ObsException("OBS не подтвердил подключение (не пришло сообщение Identified).");
        }
    }

    private Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(message.ToJsonString());
        return _socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    /// <summary>Читает одно целое текстовое сообщение (оно может прийти несколькими кадрами) и разбирает его как JSON.</summary>
    private async Task<JsonObject> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();

        try
        {
            while (true)
            {
                WebSocketReceiveResult result = await _socket
                    .ReceiveAsync(new ArraySegment<byte>(_receiveBuffer), cancellationToken)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new ObsConnectionClosedException((int?)_socket.CloseStatus, _socket.CloseStatusDescription);
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new ObsException("OBS прислал бинарное сообщение, хотя было запрошено JSON.");
                }

                buffer.Write(_receiveBuffer, 0, result.Count);
                if (buffer.Length > MaxMessageBytes)
                {
                    throw new ObsException("OBS прислал слишком большое сообщение.");
                }

                if (result.EndOfMessage) break;
            }
        }
        catch (WebSocketException ex)
        {
            throw new ObsException("Соединение с OBS оборвалось: " + ex.Message, ex);
        }

        try
        {
            // Индексатор JsonNode["op"] бросает InvalidOperationException, если корень
            // сообщения — не объект (например, массив), поэтому форму проверяем здесь.
            return JsonNode.Parse(buffer.ToArray()) as JsonObject
                   ?? throw new ObsException("OBS прислал сообщение в неожиданном формате (ожидался JSON-объект).");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ObsException("OBS прислал сообщение в неожиданном формате: " + ex.Message, ex);
        }
    }

    private static Uri BuildUri(ObsConnectionSettings settings)
    {
        string host = (settings.Host ?? string.Empty).Trim();
        if (host.Length == 0)
        {
            throw new ObsException("Не указан адрес OBS (обычно 127.0.0.1 — это тот же компьютер).");
        }

        // IPv6-адрес в URI должен быть в квадратных скобках.
        if (host.Contains(':') && !host.StartsWith('['))
        {
            host = "[" + host + "]";
        }

        if (settings.Port is < 1 or > 65535 ||
            !Uri.TryCreate($"ws://{host}:{settings.Port.ToString(CultureInfo.InvariantCulture)}/", UriKind.Absolute, out Uri? uri))
        {
            throw new ObsException($"Некорректный адрес OBS: {settings.Host}:{settings.Port}.");
        }

        return uri;
    }

    private static int? ReadInt(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out int number) ? number : null;
    }

    private static string? ReadString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }
}
