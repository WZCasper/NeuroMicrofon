using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NeuroMicrophone.Obs;
using Xunit;

namespace NeuroMicrophone.Tests.Obs;

/// <summary>
/// Проверяет сценарии APPLY и "обновить список" по итоговому состоянию
/// (поддельного) OBS: какие фильтры реально оказались на источнике и с
/// какими значениями.
/// </summary>
public class ObsServiceTests
{
    private const int AudioCaps = 2;   // OBS_SOURCE_AUDIO
    private const int VideoCaps = 1;   // OBS_SOURCE_VIDEO
    private const string Mic = "Микрофон/доп. аудио";

    private static readonly ObsService Service = new();

    private static ObsConnectionSettings Settings(FakeObsServer server) => new("127.0.0.1", server.Port, null);

    private static ObsDspSnapshot Snapshot(float gateDb = -45f, float ratio = 3f, bool denoiser = true)
        => new(denoiser, true, true, gateDb, -12f, ratio, -2f);

    [Fact]
    public async Task ListAudioInputs_SkipsVideoOnlySources_AndPutsMicrophonesFirst()
    {
        await using var server = new FakeObsServer();
        server.AddSource("Захват экрана", "monitor_capture", VideoCaps);
        server.AddSource("Яндекс браузер", "browser_source", VideoCaps | AudioCaps);
        server.AddSource(Mic, "wasapi_input_capture", AudioCaps);

        var inputs = await Service.ListAudioInputsAsync(Settings(server), CancellationToken.None);

        Assert.Equal(new[] { Mic, "Яндекс браузер" }, inputs.Select(i => i.Name).ToArray());
        Assert.True(inputs[0].IsMicrophoneLike);
        Assert.False(inputs[1].IsMicrophoneLike);
    }

    [Fact]
    public async Task Apply_OnFreshSource_CreatesFourFiltersWithPlannedSettings()
    {
        await using var server = new FakeObsServer();
        FakeSource mic = server.AddSource(Mic, "wasapi_input_capture", AudioCaps);

        ObsApplyResult result = await Service.ApplyAsync(
            Settings(server), Mic, ObsFilterPlan.Build(Snapshot(gateDb: -40f, ratio: 4f)), CancellationToken.None);

        Assert.Equal(4, mic.Filters.Count);
        Assert.Equal(4, result.Created.Count);
        Assert.Empty(result.Updated);
        Assert.Equal("31.0.0", result.ObsVersion);

        // Порядок в цепочке OBS — как порядок обработки в NeuroMicrophone.
        Assert.Equal(
            new[] { ObsFilterPlan.NoiseSuppressionName, ObsFilterPlan.NoiseGateName, ObsFilterPlan.CompressorName, ObsFilterPlan.LimiterName },
            mic.Filters.Select(f => f.Name).ToArray());

        FakeFilter gate = mic.Filter(ObsFilterPlan.NoiseGateName)!;
        Assert.Equal("noise_gate_filter", gate.Kind);
        Assert.Equal(-40.0, gate.Number("open_threshold"));
        Assert.Equal(-46.0, gate.Number("close_threshold"));

        FakeFilter comp = mic.Filter(ObsFilterPlan.CompressorName)!;
        Assert.Equal(4.0, comp.Number("ratio"));

        Assert.Equal("rnnoise", (string)mic.Filter(ObsFilterPlan.NoiseSuppressionName)!.Settings["method"]!);
        Assert.True(mic.Filters.All(f => f.Enabled));
    }

    [Fact]
    public async Task Apply_Twice_UpdatesExistingFiltersInsteadOfDuplicatingThem()
    {
        await using var server = new FakeObsServer();
        FakeSource mic = server.AddSource(Mic, "wasapi_input_capture", AudioCaps);

        await Service.ApplyAsync(Settings(server), Mic, ObsFilterPlan.Build(Snapshot(gateDb: -40f)), CancellationToken.None);
        ObsApplyResult second = await Service.ApplyAsync(Settings(server), Mic, ObsFilterPlan.Build(Snapshot(gateDb: -33f, ratio: 6f)), CancellationToken.None);

        Assert.Equal(4, mic.Filters.Count);
        Assert.Empty(second.Created);
        Assert.Equal(4, second.Updated.Count);
        Assert.Equal(-33.0, mic.Filter(ObsFilterPlan.NoiseGateName)!.Number("open_threshold"));
        Assert.Equal(6.0, mic.Filter(ObsFilterPlan.CompressorName)!.Number("ratio"));
    }

    [Fact]
    public async Task Apply_DisabledModuleInApp_CreatesDisabledFilterInObs_AndReEnablesLater()
    {
        await using var server = new FakeObsServer();
        FakeSource mic = server.AddSource(Mic, "wasapi_input_capture", AudioCaps);

        await Service.ApplyAsync(Settings(server), Mic, ObsFilterPlan.Build(Snapshot(denoiser: false)), CancellationToken.None);
        Assert.False(mic.Filter(ObsFilterPlan.NoiseSuppressionName)!.Enabled);

        await Service.ApplyAsync(Settings(server), Mic, ObsFilterPlan.Build(Snapshot(denoiser: true)), CancellationToken.None);
        Assert.True(mic.Filter(ObsFilterPlan.NoiseSuppressionName)!.Enabled);
    }

    [Fact]
    public async Task Apply_NeverTouchesUsersOwnFilters_ButWarnsAboutSameKind()
    {
        await using var server = new FakeObsServer();
        FakeSource mic = server.AddSource(Mic, "wasapi_input_capture", AudioCaps);
        var own = new FakeFilter("Мой гейт", "noise_gate_filter") { Enabled = true };
        own.Settings["open_threshold"] = -10.0;
        mic.Filters.Add(own);

        ObsApplyResult result = await Service.ApplyAsync(Settings(server), Mic, ObsFilterPlan.Build(Snapshot()), CancellationToken.None);

        Assert.Equal(5, mic.Filters.Count);
        Assert.Equal(-10.0, own.Number("open_threshold"));
        Assert.Single(result.Warnings);
        Assert.Contains("Мой гейт", result.Warnings[0]);
    }

    [Fact]
    public async Task Apply_FilterWithOurNameButWrongKind_IsRecreatedWithCorrectKind()
    {
        await using var server = new FakeObsServer();
        FakeSource mic = server.AddSource(Mic, "wasapi_input_capture", AudioCaps);
        mic.Filters.Add(new FakeFilter(ObsFilterPlan.CompressorName, "gain_filter") { Enabled = true });

        await Service.ApplyAsync(Settings(server), Mic, ObsFilterPlan.Build(Snapshot()), CancellationToken.None);

        Assert.Equal("compressor_filter", mic.Filter(ObsFilterPlan.CompressorName)!.Kind);
        Assert.Equal(1, mic.Filters.Count(f => f.Name == ObsFilterPlan.CompressorName));
    }

    [Fact]
    public async Task Apply_ToMissingSource_ExplainsAndChangesNothing()
    {
        await using var server = new FakeObsServer();
        FakeSource mic = server.AddSource(Mic, "wasapi_input_capture", AudioCaps);

        ObsException ex = await Assert.ThrowsAsync<ObsException>(
            () => Service.ApplyAsync(Settings(server), "Удалённый источник", ObsFilterPlan.Build(Snapshot()), CancellationToken.None));

        Assert.Contains("не найден", ex.Message);
        Assert.Contains("Удалённый источник", ex.Message);
        Assert.Empty(mic.Filters);
    }

    [Fact]
    public async Task Apply_WorksWithPasswordProtectedObs()
    {
        await using var server = new FakeObsServer(password: "obs-pass");
        FakeSource mic = server.AddSource(Mic, "wasapi_input_capture", AudioCaps);

        await Service.ApplyAsync(new ObsConnectionSettings("127.0.0.1", server.Port, "obs-pass"), Mic, ObsFilterPlan.Build(Snapshot()), CancellationToken.None);

        Assert.Equal(4, mic.Filters.Count);
    }
}
