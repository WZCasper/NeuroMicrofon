using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NeuroMicrophone.Obs;
using Xunit;

namespace NeuroMicrophone.Tests.Obs;

/// <summary>
/// Проверяет клиент протокола obs-websocket v5 на поддельном сервере,
/// который ведёт себя по документации: рукопожатие, авторизация,
/// запросы/ответы, ошибки, разбиение сообщений на кадры.
/// </summary>
public class ObsWebSocketClientTests
{
    private static ObsConnectionSettings Settings(FakeObsServer server, string? password = null)
        => new("127.0.0.1", server.Port, password);

    [Fact]
    public void ComputeAuthentication_MatchesIndependentlyComputedReference()
    {
        // Эталон посчитан отдельно (Python hashlib) по алгоритму из документации
        // obs-websocket: base64(SHA256(base64(SHA256(пароль+salt)) + challenge)).
        string auth = ObsWebSocketClient.ComputeAuthentication(
            "supersecretpassword",
            "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=",
            "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=");

        Assert.Equal("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=", auth);
    }

    [Fact]
    public async Task Connect_WithoutPassword_ReadsVersionsFromHello()
    {
        await using var server = new FakeObsServer();

        await using ObsWebSocketClient client = await ObsWebSocketClient.ConnectAsync(Settings(server), CancellationToken.None);

        Assert.Equal("31.0.0", client.ObsStudioVersion);
        Assert.Equal("5.5.4", client.ObsWebSocketVersion);
    }

    [Fact]
    public async Task Connect_WithCorrectPassword_Succeeds()
    {
        await using var server = new FakeObsServer(password: "pa$$ w0rd Привет");

        await using ObsWebSocketClient client = await ObsWebSocketClient.ConnectAsync(Settings(server, "pa$$ w0rd Привет"), CancellationToken.None);

        JsonObject response = await client.SendRequestAsync("GetInputList", null, CancellationToken.None);
        Assert.NotNull(response["inputs"]);
    }

    [Fact]
    public async Task Connect_WithWrongPassword_ReportsWrongPassword()
    {
        await using var server = new FakeObsServer(password: "right");

        ObsException ex = await Assert.ThrowsAsync<ObsException>(
            () => ObsWebSocketClient.ConnectAsync(Settings(server, "wrong"), CancellationToken.None));

        Assert.Contains("Неверный пароль", ex.Message);
    }

    [Fact]
    public async Task Connect_WhenServerNeedsPasswordButNoneGiven_AsksForPassword()
    {
        await using var server = new FakeObsServer(password: "secret");

        ObsException ex = await Assert.ThrowsAsync<ObsException>(
            () => ObsWebSocketClient.ConnectAsync(Settings(server, password: null), CancellationToken.None));

        Assert.Contains("требует пароль", ex.Message);
    }

    [Fact]
    public async Task Connect_ToClosedPort_ExplainsHowToEnableObsServer()
    {
        int closedPort;
        await using (var temporary = new FakeObsServer())
        {
            closedPort = temporary.Port;
        }

        ObsException ex = await Assert.ThrowsAsync<ObsException>(
            () => ObsWebSocketClient.ConnectAsync(new ObsConnectionSettings("127.0.0.1", closedPort, null), CancellationToken.None));

        Assert.Contains("Не удалось подключиться к OBS", ex.Message);
        Assert.Contains("WebSocket", ex.Message);
    }

    [Fact]
    public async Task Connect_WithInvalidPort_FailsWithClearMessage()
    {
        ObsException ex = await Assert.ThrowsAsync<ObsException>(
            () => ObsWebSocketClient.ConnectAsync(new ObsConnectionSettings("127.0.0.1", 70000, null), CancellationToken.None));

        Assert.Contains("Некорректный адрес", ex.Message);
    }

    [Fact]
    public async Task SendRequest_RejectedByObs_ThrowsWithCodeAndComment()
    {
        await using var server = new FakeObsServer();
        await using ObsWebSocketClient client = await ObsWebSocketClient.ConnectAsync(Settings(server), CancellationToken.None);

        ObsRequestException ex = await Assert.ThrowsAsync<ObsRequestException>(
            () => client.SendRequestAsync("GetSourceFilterList", new JsonObject { ["sourceName"] = "нет такого" }, CancellationToken.None));

        Assert.Equal(ObsRequestException.ResourceNotFoundCode, ex.Code);
        Assert.Equal("GetSourceFilterList", ex.RequestType);
    }

    [Fact]
    public async Task SendRequest_SkipsForeignEvents_AndReassemblesFragmentedResponses()
    {
        await using var server = new FakeObsServer
        {
            SplitResponsesIntoFrames = true,
            SendEventBeforeEachResponse = true,
        };
        server.AddSource("Микрофон/доп. аудио", "wasapi_input_capture", caps: 2);

        await using ObsWebSocketClient client = await ObsWebSocketClient.ConnectAsync(Settings(server), CancellationToken.None);
        JsonObject response = await client.SendRequestAsync("GetInputList", null, CancellationToken.None);

        JsonArray inputs = (JsonArray)response["inputs"]!;
        Assert.Equal("Микрофон/доп. аудио", (string)inputs[0]!["inputName"]!);
    }

    [Fact]
    public async Task SendRequest_HandlesResponsesLargerThanReceiveBuffer()
    {
        await using var server = new FakeObsServer();
        for (int i = 0; i < 600; i++)
        {
            server.AddSource($"Источник номер {i:D4} с длинным названием для проверки буфера", "wasapi_input_capture", caps: 2);
        }

        await using ObsWebSocketClient client = await ObsWebSocketClient.ConnectAsync(Settings(server), CancellationToken.None);
        JsonObject response = await client.SendRequestAsync("GetInputList", null, CancellationToken.None);

        Assert.Equal(600, ((JsonArray)response["inputs"]!).Count);
    }

    [Fact]
    public async Task SendRequest_SeveralRequestsOnOneConnection_GetTheirOwnAnswers()
    {
        await using var server = new FakeObsServer();
        server.AddSource("A", "wasapi_input_capture", caps: 2);

        await using ObsWebSocketClient client = await ObsWebSocketClient.ConnectAsync(Settings(server), CancellationToken.None);
        JsonObject first = await client.SendRequestAsync("GetInputList", null, CancellationToken.None);
        JsonObject second = await client.SendRequestAsync("GetSourceFilterList", new JsonObject { ["sourceName"] = "A" }, CancellationToken.None);

        Assert.NotNull(first["inputs"]);
        Assert.NotNull(second["filters"]);
        Assert.Equal(new[] { "GetInputList", "GetSourceFilterList" }, server.RequestLog.ToArray());
    }
}
