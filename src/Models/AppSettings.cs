namespace NeuroMicrophone.Models;

/// <summary>
/// Всё, что сохраняется между запусками в %LocalAppData%\NeuroMicrophone\settings.json
/// через SettingsService. Все поля — простые значения, без ссылок на живые
/// COM/аудио-объекты, поэтому структуру можно свободно (де)сериализовать
/// через System.Text.Json.
/// </summary>
public sealed class AppSettings
{
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public string? MonitorDeviceId { get; set; }

    public float GateThresholdDb { get; set; } = -50f;
    public float DenoiserWetMix { get; set; } = 1f;
    public float AgcTargetLevelDb { get; set; } = -18f;
    public float CompressorThresholdDb { get; set; } = -12f;
    public float CompressorRatio { get; set; } = 3f;
    public float HighPassCutoffHz { get; set; } = 90f;

    public bool LaunchOnStartup { get; set; }

    /// <summary>
    /// Модификаторы и виртуальный код клавиши выбранной горячей клавиши
    /// заглушки микрофона (см. Models.HotkeyOption/HotkeyModifiers).
    /// 0/0 означает "используется вариант по умолчанию".
    /// </summary>
    public uint HotkeyModifiers { get; set; }
    public uint HotkeyVirtualKey { get; set; }

    /// <summary>
    /// "Опубликованное" имя INF виртуального драйвера в хранилище драйверов
    /// Windows (вида oemNN.inf), полученное во время установки — используется
    /// для надёжного удаления драйвера через pnputil (см. DriverInstaller).
    /// </summary>
    public string? PublishedDriverInfName { get; set; }

    public bool HasDspSettings { get; set; }
}
