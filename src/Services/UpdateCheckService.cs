using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace NeuroMicrophone.Services;

/// <summary>Результат успешной проверки: обновление доступно.</summary>
public sealed class UpdateCheckResult
{
    public Version NewVersion { get; }
    public string ReleaseUrl { get; }

    public UpdateCheckResult(Version newVersion, string releaseUrl)
    {
        NewVersion = newVersion;
        ReleaseUrl = releaseUrl;
    }
}

/// <summary>
/// Сверяет версию текущей сборки (версия сборки .exe, проставляемая при
/// публикации в CI как 1.0.&lt;номер запуска&gt;) с последним релизом в
/// GitHub Releases репозитория проекта. При сбое сети/разбора — просто
/// возвращает null и ничего не сообщает пользователю: проверка обновлений
/// не должна мешать основной работе приложения ни при каких условиях.
/// </summary>
public sealed class UpdateCheckService
{
    // Если репозиторий когда-нибудь переедет/переименуется — поменять только эту строку.
    private const string ReleasesLatestApiUrl = "https://api.github.com/repos/WZCasper/NeuroMicrofon/releases/latest";

    public async Task<UpdateCheckResult?> CheckForUpdateAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NeuroMicrophone-UpdateCheck");

            using HttpResponseMessage response = await client.GetAsync(ReleasesLatestApiUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("tag_name", out JsonElement tagElement)) return null;
            if (!document.RootElement.TryGetProperty("html_url", out JsonElement urlElement)) return null;

            string? tagName = tagElement.GetString();
            string? releaseUrl = urlElement.GetString();
            if (string.IsNullOrEmpty(tagName) || string.IsNullOrEmpty(releaseUrl)) return null;

            string versionText = tagName.TrimStart('v', 'V');
            if (!Version.TryParse(versionText, out Version? remoteVersion)) return null;

            Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

            return remoteVersion > currentVersion ? new UpdateCheckResult(remoteVersion, releaseUrl) : null;
        }
        catch (Exception)
        {
            // Нет сети, GitHub недоступен, неожиданный формат ответа и т.п. —
            // проверка обновлений необязательна и не должна ничего ломать.
            return null;
        }
    }
}
