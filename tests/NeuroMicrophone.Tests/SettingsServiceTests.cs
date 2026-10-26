using System;
using System.IO;
using System.Threading.Tasks;
using NeuroMicrophone.Models;
using NeuroMicrophone.Services;
using Xunit;

namespace NeuroMicrophone.Tests;

/// <summary>
/// SettingsService раньше не был покрыт тестами вовсе — у него не было
/// способа указать путь к файлу настроек, кроме реального
/// %LocalAppData%\NeuroMicrophone, который трогать в тестах нельзя.
/// Перегрузка конструктора с явным путём (см. SettingsService.cs) решает
/// это: каждый тест работает со своим временным файлом во временной папке
/// и не способен задеть ни настоящие настройки пользователя, ни другие
/// тесты, выполняющиеся параллельно.
/// </summary>
public class SettingsServiceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _settingsFilePath;

    public SettingsServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"nm_settings_test_{Guid.NewGuid():N}");
        _settingsFilePath = Path.Combine(_tempDirectory, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (Exception)
        {
            // Временная папка теста — не критично, если её не удалось убрать.
        }
    }

    [Fact]
    public async Task LoadAsync_FileDoesNotExist_ReturnsNull()
    {
        var service = new SettingsService(_settingsFilePath);

        AppSettings? result = await service.LoadAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsAllFields()
    {
        var service = new SettingsService(_settingsFilePath);
        var original = new AppSettings
        {
            InputDeviceId = "{mic-guid}",
            OutputDeviceId = "{speaker-guid}",
            MonitorDeviceId = null,
            GateThresholdDb = -37.5f,
            DenoiserWetMix = 0.75f,
            AgcTargetLevelDb = -16f,
            CompressorThresholdDb = -11f,
            CompressorRatio = 4f,
            HighPassCutoffHz = 110f,
            LaunchOnStartup = true,
            HasShownTrayMinimizeHint = true,
            ObsHost = "192.168.1.50",
            ObsPort = 4456,
            ObsSourceName = "Микрофон/доп. аудио",
            HotkeyModifiers = 3,
            HotkeyVirtualKey = 0x4D,
            PublishedDriverInfName = "oem12.inf",
            HasDspSettings = true,
            HasCalibrationResult = true,
            CalibratedGateThresholdDb = -40f,
            CalibratedWetMix = 1f,
            CalibratedCompThresholdDb = -12f,
            CalibratedCompRatio = 3.5f,
        };

        bool saveResult = await service.SaveAsync(original);
        AppSettings? loaded = await service.LoadAsync();

        Assert.True(saveResult);
        Assert.NotNull(loaded);
        Assert.Equal(original.InputDeviceId, loaded!.InputDeviceId);
        Assert.Equal(original.OutputDeviceId, loaded.OutputDeviceId);
        Assert.Equal(original.MonitorDeviceId, loaded.MonitorDeviceId);
        Assert.Equal(original.GateThresholdDb, loaded.GateThresholdDb);
        Assert.Equal(original.DenoiserWetMix, loaded.DenoiserWetMix);
        Assert.Equal(original.AgcTargetLevelDb, loaded.AgcTargetLevelDb);
        Assert.Equal(original.CompressorThresholdDb, loaded.CompressorThresholdDb);
        Assert.Equal(original.CompressorRatio, loaded.CompressorRatio);
        Assert.Equal(original.HighPassCutoffHz, loaded.HighPassCutoffHz);
        Assert.Equal(original.LaunchOnStartup, loaded.LaunchOnStartup);
        Assert.Equal(original.HasShownTrayMinimizeHint, loaded.HasShownTrayMinimizeHint);
        Assert.Equal(original.ObsHost, loaded.ObsHost);
        Assert.Equal(original.ObsPort, loaded.ObsPort);
        Assert.Equal(original.ObsSourceName, loaded.ObsSourceName);
        Assert.Equal(original.HotkeyModifiers, loaded.HotkeyModifiers);
        Assert.Equal(original.HotkeyVirtualKey, loaded.HotkeyVirtualKey);
        Assert.Equal(original.PublishedDriverInfName, loaded.PublishedDriverInfName);
        Assert.Equal(original.HasDspSettings, loaded.HasDspSettings);
        Assert.Equal(original.HasCalibrationResult, loaded.HasCalibrationResult);
        Assert.Equal(original.CalibratedGateThresholdDb, loaded.CalibratedGateThresholdDb);
        Assert.Equal(original.CalibratedWetMix, loaded.CalibratedWetMix);
        Assert.Equal(original.CalibratedCompThresholdDb, loaded.CalibratedCompThresholdDb);
        Assert.Equal(original.CalibratedCompRatio, loaded.CalibratedCompRatio);
    }

    [Fact]
    public async Task SaveAsync_Twice_SecondSaveOverwritesFirst_AndLeavesNoTempFileBehind()
    {
        var service = new SettingsService(_settingsFilePath);

        await service.SaveAsync(new AppSettings { GateThresholdDb = -50f });
        await service.SaveAsync(new AppSettings { GateThresholdDb = -30f });

        AppSettings? loaded = await service.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal(-30f, loaded!.GateThresholdDb);

        // Запись идёт через временный файл с последующим атомарным
        // переименованием (см. комментарий в SettingsService.SaveAsync) —
        // после успешного сохранения в папке не должно оставаться ничего,
        // кроме самого settings.json.
        string[] filesInDirectory = Directory.GetFiles(_tempDirectory);
        Assert.Single(filesInDirectory);
        Assert.Equal(_settingsFilePath, filesInDirectory[0]);
    }

    [Fact]
    public async Task LoadAsync_CorruptedFile_ReturnsNullInsteadOfThrowing()
    {
        Directory.CreateDirectory(_tempDirectory);
        await File.WriteAllTextAsync(_settingsFilePath, "{ это не корректный JSON ");
        var service = new SettingsService(_settingsFilePath);

        AppSettings? result = await service.LoadAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_DefaultValuesAlsoRoundTrip()
    {
        // Отдельно от теста с заполненными полями: убеждаемся, что и
        // значения по умолчанию (в т.ч. null-строки, false, значения с
        // плавающей точкой по умолчанию) переживают сериализацию.
        var service = new SettingsService(_settingsFilePath);
        var defaults = new AppSettings();

        await service.SaveAsync(defaults);
        AppSettings? loaded = await service.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Null(loaded!.InputDeviceId);
        Assert.False(loaded.LaunchOnStartup);
        Assert.False(loaded.HasCalibrationResult);
        Assert.Equal(defaults.GateThresholdDb, loaded.GateThresholdDb);
        Assert.Equal(defaults.ObsHost, loaded.ObsHost);
        Assert.Equal(defaults.ObsPort, loaded.ObsPort);
    }
}
