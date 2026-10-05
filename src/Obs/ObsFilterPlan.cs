using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace NeuroMicrophone.Obs;

/// <summary>
/// Снимок настроек DSP-цепочки NeuroMicrophone, которые можно перенести в
/// фильтры OBS. Простой набор значений (без ссылок на ViewModel/аудиодвижок),
/// чтобы расчёт фильтров можно было проверить обычным юнит-тестом.
/// </summary>
public sealed record ObsDspSnapshot(
    bool DenoiserEnabled,
    bool GateEnabled,
    bool CompressorEnabled,
    float GateThresholdDb,
    float CompressorThresholdDb,
    float CompressorRatio,
    float LimiterCeilingDb);

/// <summary>
/// Один фильтр OBS, который нужно создать/обновить на выбранном источнике:
/// стабильное имя (по нему программа находит свой фильтр при повторном
/// нажатии APPLY и обновляет его, а не плодит дубликаты), тип фильтра OBS,
/// значения параметров и признак "включён".
/// </summary>
public sealed class ObsFilterSpec
{
    public string Name { get; }
    public string Kind { get; }

    /// <summary>Короткое название для отчёта пользователю ("Шумовой гейт" и т.п.).</summary>
    public string Label { get; }

    public bool Enabled { get; }

    /// <summary>
    /// Параметры фильтра в виде значений double/int/string/bool. Значения
    /// хранятся не как JsonNode: узел JSON можно присоединить только к одному
    /// родителю, поэтому каждый запрос строит свежий объект через
    /// CreateSettingsJson().
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, object>> Settings { get; }

    public ObsFilterSpec(string name, string kind, string label, bool enabled, IReadOnlyList<KeyValuePair<string, object>> settings)
    {
        Name = name;
        Kind = kind;
        Label = label;
        Enabled = enabled;
        Settings = settings;
    }

    public JsonObject CreateSettingsJson()
    {
        var json = new JsonObject();
        foreach (KeyValuePair<string, object> setting in Settings)
        {
            json[setting.Key] = setting.Value switch
            {
                double number => JsonValue.Create(number),
                int integer => JsonValue.Create(integer),
                string text => JsonValue.Create(text),
                bool flag => JsonValue.Create(flag),
                _ => throw new InvalidOperationException($"Неподдерживаемый тип параметра фильтра OBS: {setting.Value.GetType().Name}"),
            };
        }

        return json;
    }
}

/// <summary>
/// Переносит настройки NeuroMicrophone на встроенные фильтры OBS Studio.
/// Идентификаторы фильтров и имена параметров сверены с исходным кодом OBS
/// (plugins/obs-filters): noise_suppress_filter_v2, noise_gate_filter,
/// compressor_filter, limiter_filter.
///
/// Что переносится и что нет — честно:
///  • Шумоподавление — RNNoise в OBS (тот же алгоритм); у фильтра OBS нет
///    регулировки "силы", поэтому микс (wet) из NeuroMicrophone не переносится,
///    переносится только включено/выключено.
///  • Шумовой гейт, компрессор, лимитер — переносятся пороги/коэффициент;
///    времена атаки/удержания/спада берутся такими же, как в собственной
///    цепочке NeuroMicrophone.
///  • AGC и фильтр верхних частот в OBS аналога не имеют — не переносятся
///    (имитировать их статическим усилением было бы неправдой).
/// Порядок фильтров совпадает с порядком обработки в NeuroMicrophone:
/// шумоподавление → гейт → компрессор → лимитер.
/// </summary>
public static class ObsFilterPlan
{
    public const string NamePrefix = "NeuroMic: ";

    public const string NoiseSuppressionName = NamePrefix + "Noise Suppression";
    public const string NoiseGateName = NamePrefix + "Noise Gate";
    public const string CompressorName = NamePrefix + "Compressor";
    public const string LimiterName = NamePrefix + "Limiter";

    // Времена — те же значения по умолчанию, что в собственных классах
    // NoiseGate (5/100/150 мс), Compressor (10/150 мс) и Limiter (100 мс).
    private const int GateAttackMs = 5;
    private const int GateHoldMs = 100;
    private const int GateReleaseMs = 150;
    private const int CompressorAttackMs = 10;
    private const int CompressorReleaseMs = 150;
    private const int LimiterReleaseMs = 100;

    // Гистерезис гейта: порог закрытия ниже порога открытия — как в фильтре OBS по умолчанию (-26/-32 дБ).
    private const double GateHysteresisDb = 6.0;

    public static IReadOnlyList<ObsFilterSpec> Build(ObsDspSnapshot snapshot)
    {
        double gateOpen = Clamp(snapshot.GateThresholdDb, -96.0, 0.0);
        double gateClose = Clamp(gateOpen - GateHysteresisDb, -96.0, 0.0);

        return new List<ObsFilterSpec>
        {
            new(NoiseSuppressionName, "noise_suppress_filter_v2", "Шумоподавление", snapshot.DenoiserEnabled,
                new List<KeyValuePair<string, object>>
                {
                    new("method", "rnnoise"),
                }),

            new(NoiseGateName, "noise_gate_filter", "Шумовой гейт", snapshot.GateEnabled,
                new List<KeyValuePair<string, object>>
                {
                    new("open_threshold", gateOpen),
                    new("close_threshold", gateClose),
                    new("attack_time", GateAttackMs),
                    new("hold_time", GateHoldMs),
                    new("release_time", GateReleaseMs),
                }),

            new(CompressorName, "compressor_filter", "Компрессор", snapshot.CompressorEnabled,
                new List<KeyValuePair<string, object>>
                {
                    new("ratio", Clamp(snapshot.CompressorRatio, 1.0, 32.0)),
                    new("threshold", Clamp(snapshot.CompressorThresholdDb, -60.0, 0.0)),
                    new("attack_time", CompressorAttackMs),
                    new("release_time", CompressorReleaseMs),
                    new("output_gain", 0.0),
                }),

            // Лимитер в NeuroMicrophone включён всегда (защита от клиппинга), поэтому и в OBS — всегда.
            new(LimiterName, "limiter_filter", "Лимитер", true,
                new List<KeyValuePair<string, object>>
                {
                    new("threshold", Clamp(snapshot.LimiterCeilingDb, -60.0, 0.0)),
                    new("release_time", LimiterReleaseMs),
                }),
        };
    }

    private static double Clamp(float value, double min, double max)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return min;
        return Math.Round(Math.Clamp((double)value, min, max), 1);
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Round(Math.Clamp(value, min, max), 1);
    }
}
