using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroMicrophone.Obs;

/// <summary>
/// Сценарии работы с OBS поверх ObsWebSocketClient: получить список
/// аудиоисточников и применить к выбранному источнику настройки
/// NeuroMicrophone в виде фильтров OBS. Каждый вызов открывает собственное
/// короткое соединение (см. комментарий в ObsWebSocketClient), поэтому класс
/// не хранит состояния и безопасен для вызова из любого потока.
/// </summary>
public sealed class ObsService
{
    // OBS_SOURCE_AUDIO = (1 << 1) из libobs/obs-source.h — флаг "источник
    // производит звук"; в GetInputList он приходит в поле inputKindCaps.
    private const int ObsSourceAudioFlag = 1 << 1;

    // Общий предел на всю операцию (подключение + все запросы), чтобы
    // зависший OBS не оставлял кнопку в состоянии "выполняется" навсегда.
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);

    public async Task<IReadOnlyList<ObsInputInfo>> ListAudioInputsAsync(ObsConnectionSettings settings, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CreateTimeoutSource(cancellationToken);
        try
        {
            await using ObsWebSocketClient client = await ObsWebSocketClient.ConnectAsync(settings, timeout.Token).ConfigureAwait(false);
            JsonObject response = await client.SendRequestAsync("GetInputList", null, timeout.Token).ConfigureAwait(false);

            var inputs = new List<ObsInputInfo>();
            if (response["inputs"] is JsonArray array)
            {
                foreach (JsonNode? item in array)
                {
                    if (item is not JsonObject input) continue;

                    string? name = ReadString(input["inputName"]);
                    if (string.IsNullOrEmpty(name)) continue;

                    // Если OBS не прислал inputKindCaps (старые версии протокола) —
                    // источник оставляем в списке: лучше показать лишний, чем потерять микрофон.
                    int? caps = ReadInt(input["inputKindCaps"]);
                    if (caps.HasValue && (caps.Value & ObsSourceAudioFlag) == 0) continue;

                    inputs.Add(new ObsInputInfo(name, ReadString(input["inputKind"]) ?? ""));
                }
            }

            return inputs
                .OrderBy(i => i.IsMicrophoneLike ? 0 : 1)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw CreateTimeoutException();
        }
    }

    /// <summary>
    /// Создаёт (или обновляет, если они уже были созданы этой программой
    /// ранее) фильтры на источнике. Свои фильтры находятся по стабильным
    /// именам с префиксом "NeuroMic: " — повторное нажатие APPLY обновляет
    /// значения, а не плодит дубликаты. Чужие фильтры пользователя не
    /// изменяются и не удаляются.
    /// </summary>
    public async Task<ObsApplyResult> ApplyAsync(
        ObsConnectionSettings settings,
        string sourceName,
        IReadOnlyList<ObsFilterSpec> filters,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CreateTimeoutSource(cancellationToken);
        try
        {
            await using ObsWebSocketClient client = await ObsWebSocketClient.ConnectAsync(settings, timeout.Token).ConfigureAwait(false);
            CancellationToken token = timeout.Token;

            List<ExistingFilter> existing = await ReadFiltersAsync(client, sourceName, token).ConfigureAwait(false);

            var created = new List<string>();
            var updated = new List<string>();
            var warnings = new List<string>();

            foreach (ObsFilterSpec spec in filters)
            {
                ExistingFilter? current = existing.FirstOrDefault(f => f.Name == spec.Name);

                // Фильтр с нашим именем, но другого типа — не наш (или OBS поменял версию
                // типа). Имя с префиксом "NeuroMic: " принадлежит этой программе, поэтому
                // пересоздаём его, а не оставляем в неработающем виде.
                if (current != null && current.Kind != spec.Kind)
                {
                    await client.SendRequestAsync("RemoveSourceFilter", new JsonObject
                    {
                        ["sourceName"] = sourceName,
                        ["filterName"] = spec.Name,
                    }, token).ConfigureAwait(false);
                    current = null;
                }

                if (current == null)
                {
                    await client.SendRequestAsync("CreateSourceFilter", new JsonObject
                    {
                        ["sourceName"] = sourceName,
                        ["filterName"] = spec.Name,
                        ["filterKind"] = spec.Kind,
                        ["filterSettings"] = spec.CreateSettingsJson(),
                    }, token).ConfigureAwait(false);
                    created.Add(spec.Label);

                    // Новый фильтр OBS создаёт включённым — отдельный запрос нужен, только чтобы выключить.
                    if (!spec.Enabled)
                    {
                        await SetEnabledAsync(client, sourceName, spec, token).ConfigureAwait(false);
                    }
                }
                else
                {
                    await client.SendRequestAsync("SetSourceFilterSettings", new JsonObject
                    {
                        ["sourceName"] = sourceName,
                        ["filterName"] = spec.Name,
                        ["filterSettings"] = spec.CreateSettingsJson(),
                        ["overlay"] = true,
                    }, token).ConfigureAwait(false);
                    await SetEnabledAsync(client, sourceName, spec, token).ConfigureAwait(false);
                    updated.Add(spec.Label);
                }

                // Чужой фильтр того же типа на этом источнике — звук обработается дважды.
                foreach (ExistingFilter other in existing)
                {
                    if (other.Kind == spec.Kind && other.Name != spec.Name && !other.Name.StartsWith(ObsFilterPlan.NamePrefix, StringComparison.Ordinal))
                    {
                        warnings.Add($"На источнике уже есть фильтр «{other.Name}» того же типа ({spec.Label.ToLowerInvariant()}) — звук может обрабатываться дважды.");
                    }
                }
            }

            return new ObsApplyResult(sourceName, client.ObsStudioVersion, created, updated, warnings);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw CreateTimeoutException();
        }
    }

    private static async Task<List<ExistingFilter>> ReadFiltersAsync(ObsWebSocketClient client, string sourceName, CancellationToken token)
    {
        JsonObject response;
        try
        {
            response = await client.SendRequestAsync("GetSourceFilterList", new JsonObject { ["sourceName"] = sourceName }, token).ConfigureAwait(false);
        }
        catch (ObsRequestException ex) when (ex.Code == ObsRequestException.ResourceNotFoundCode)
        {
            throw new ObsException(
                $"Источник «{sourceName}» не найден в OBS — возможно, его переименовали или удалили. " +
                "Нажмите «ОБНОВИТЬ» и выберите источник заново.", ex);
        }

        var filters = new List<ExistingFilter>();
        if (response["filters"] is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is not JsonObject filter) continue;

                string? name = ReadString(filter["filterName"]);
                string? kind = ReadString(filter["filterKind"]);
                if (name == null || kind == null) continue;

                filters.Add(new ExistingFilter(name, kind));
            }
        }

        return filters;
    }

    private static Task SetEnabledAsync(ObsWebSocketClient client, string sourceName, ObsFilterSpec spec, CancellationToken token)
    {
        return client.SendRequestAsync("SetSourceFilterEnabled", new JsonObject
        {
            ["sourceName"] = sourceName,
            ["filterName"] = spec.Name,
            ["filterEnabled"] = spec.Enabled,
        }, token);
    }

    private static CancellationTokenSource CreateTimeoutSource(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(OperationTimeout);
        return source;
    }

    private static ObsException CreateTimeoutException()
    {
        return new ObsException(
            $"OBS не ответил за {OperationTimeout.TotalSeconds:F0} с. Проверьте, что OBS не завис " +
            "и что в нём открыто не модальное окно (например, диалог настроек).");
    }

    private static int? ReadInt(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out int number) ? number : null;
    }

    private static string? ReadString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }

    private sealed record ExistingFilter(string Name, string Kind);
}
