using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NeuroMicrophone.Services;
using Xunit;

namespace NeuroMicrophone.Tests;

/// <summary>
/// Проверяет UpdateCheckService через подменённый HttpMessageHandler (см.
/// конструктор для тестов в UpdateCheckService.cs) — ни один из этих
/// тестов не делает настоящего обращения к api.github.com.
/// </summary>
public class UpdateCheckServiceTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string? _jsonBody;
        private readonly Exception? _throwOnSend;

        public FakeHandler(HttpStatusCode statusCode, string? jsonBody)
        {
            _statusCode = statusCode;
            _jsonBody = jsonBody;
        }

        public FakeHandler(Exception throwOnSend)
        {
            _throwOnSend = throwOnSend;
            _statusCode = HttpStatusCode.OK;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_throwOnSend != null) throw _throwOnSend;

            var response = new HttpResponseMessage(_statusCode);
            if (_jsonBody != null)
            {
                response.Content = new StringContent(_jsonBody);
            }
            return Task.FromResult(response);
        }
    }

    private static Version CurrentAssemblyVersion =>
        Assembly.GetAssembly(typeof(UpdateCheckService))!.GetName().Version ?? new Version(0, 0, 0, 0);

    private static string ReleaseJson(Version version) =>
        "{\"tag_name\": \"v" + version + "\", \"html_url\": \"https://github.com/WZCasper/NeuroMicrofon/releases/tag/v" + version + "\"}";

    [Fact]
    public async Task CheckForUpdateAsync_NewerVersionAvailable_ReturnsResult()
    {
        // Берём версию относительно реальной версии сборки теста, а не
        // захардкоженное число — иначе тест был бы хрупким к тому, какая
        // версия сейчас проставлена в .csproj или передана через /p:Version.
        var newer = new Version(CurrentAssemblyVersion.Major + 1, 0, 0, 0);
        var service = new UpdateCheckService(new FakeHandler(HttpStatusCode.OK, ReleaseJson(newer)));

        UpdateCheckResult? result = await service.CheckForUpdateAsync();

        Assert.NotNull(result);
        Assert.Equal(newer, result!.NewVersion);
        Assert.Contains("github.com/WZCasper/NeuroMicrofon", result.ReleaseUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_SameOrOlderVersion_ReturnsNull()
    {
        var olderVersion = new Version(0, 0, 1, 0); // заведомо старше любой реальной версии сборки быть не может
        var service = new UpdateCheckService(new FakeHandler(HttpStatusCode.OK, ReleaseJson(olderVersion)));

        UpdateCheckResult? result = await service.CheckForUpdateAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckForUpdateAsync_HttpErrorStatus_ReturnsNull()
    {
        var service = new UpdateCheckService(new FakeHandler(HttpStatusCode.NotFound, jsonBody: null));

        UpdateCheckResult? result = await service.CheckForUpdateAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckForUpdateAsync_MalformedJson_ReturnsNullInsteadOfThrowing()
    {
        var service = new UpdateCheckService(new FakeHandler(HttpStatusCode.OK, "это не JSON"));

        UpdateCheckResult? result = await service.CheckForUpdateAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckForUpdateAsync_MissingTagName_ReturnsNull()
    {
        var service = new UpdateCheckService(new FakeHandler(HttpStatusCode.OK, "{\"html_url\": \"https://example.com\"}"));

        UpdateCheckResult? result = await service.CheckForUpdateAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckForUpdateAsync_NetworkFailure_ReturnsNullInsteadOfThrowing()
    {
        var service = new UpdateCheckService(new FakeHandler(new HttpRequestException("DNS не резолвится")));

        UpdateCheckResult? result = await service.CheckForUpdateAsync();

        Assert.Null(result);
    }
}
