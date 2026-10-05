using System;
using System.Linq;
using System.Text.Json.Nodes;
using NeuroMicrophone.Obs;
using Xunit;

namespace NeuroMicrophone.Tests.Obs;

/// <summary>
/// Проверяет перенос настроек NeuroMicrophone в фильтры OBS: имена и типы
/// фильтров (по исходникам OBS), порядок цепочки, диапазоны значений и то,
/// что мусорные значения (NaN, выход за диапазон) не уходят в OBS как есть.
/// </summary>
public class ObsFilterPlanTests
{
    private static ObsDspSnapshot Snapshot(
        bool denoiser = true, bool gate = true, bool comp = true,
        float gateDb = -45f, float compThresholdDb = -12f, float compRatio = 3f, float limiterDb = -2f)
        => new(denoiser, gate, comp, gateDb, compThresholdDb, compRatio, limiterDb);

    [Fact]
    public void Build_ReturnsFourFiltersInSignalOrder()
    {
        var plan = ObsFilterPlan.Build(Snapshot());

        Assert.Equal(
            new[] { "noise_suppress_filter_v2", "noise_gate_filter", "compressor_filter", "limiter_filter" },
            plan.Select(f => f.Kind).ToArray());
    }

    [Fact]
    public void Build_AllNamesCarryProgramPrefix_SoRepeatedApplyFindsThem()
    {
        var plan = ObsFilterPlan.Build(Snapshot());

        Assert.True(plan.All(f => f.Name.StartsWith(ObsFilterPlan.NamePrefix, StringComparison.Ordinal)));
        Assert.Equal(plan.Count, plan.Select(f => f.Name).Distinct().Count());
    }

    [Fact]
    public void Build_Gate_UsesThresholdAsOpenAndLowerCloseThreshold()
    {
        var gate = ObsFilterPlan.Build(Snapshot(gateDb: -45f)).Single(f => f.Kind == "noise_gate_filter");
        JsonObject json = gate.CreateSettingsJson();

        Assert.Equal(-45.0, json["open_threshold"]!.GetValue<double>());
        Assert.Equal(-51.0, json["close_threshold"]!.GetValue<double>());
        Assert.True(json["attack_time"]!.GetValue<int>() >= 0);
    }

    [Fact]
    public void Build_Gate_CloseThresholdNeverLeavesObsRange()
    {
        var gate = ObsFilterPlan.Build(Snapshot(gateDb: -96f)).Single(f => f.Kind == "noise_gate_filter");

        Assert.Equal(-96.0, gate.CreateSettingsJson()["close_threshold"]!.GetValue<double>());
    }

    [Fact]
    public void Build_Compressor_CarriesRatioAndThreshold()
    {
        var comp = ObsFilterPlan.Build(Snapshot(compThresholdDb: -20f, compRatio: 4.5f)).Single(f => f.Kind == "compressor_filter");
        JsonObject json = comp.CreateSettingsJson();

        Assert.Equal(4.5, json["ratio"]!.GetValue<double>());
        Assert.Equal(-20.0, json["threshold"]!.GetValue<double>());
    }

    [Fact]
    public void Build_Limiter_IsAlwaysEnabled_EvenIfOtherModulesAreOff()
    {
        var plan = ObsFilterPlan.Build(Snapshot(denoiser: false, gate: false, comp: false));

        Assert.True(plan.Single(f => f.Kind == "limiter_filter").Enabled);
        Assert.False(plan.Single(f => f.Kind == "noise_suppress_filter_v2").Enabled);
        Assert.False(plan.Single(f => f.Kind == "noise_gate_filter").Enabled);
        Assert.False(plan.Single(f => f.Kind == "compressor_filter").Enabled);
    }

    [Fact]
    public void Build_ClampsValuesToRangesAcceptedByObs()
    {
        var plan = ObsFilterPlan.Build(Snapshot(gateDb: 12f, compThresholdDb: -200f, compRatio: 99f, limiterDb: 5f));

        Assert.Equal(0.0, plan.Single(f => f.Kind == "noise_gate_filter").CreateSettingsJson()["open_threshold"]!.GetValue<double>());

        JsonObject comp = plan.Single(f => f.Kind == "compressor_filter").CreateSettingsJson();
        Assert.Equal(-60.0, comp["threshold"]!.GetValue<double>());
        Assert.Equal(32.0, comp["ratio"]!.GetValue<double>());

        Assert.Equal(0.0, plan.Single(f => f.Kind == "limiter_filter").CreateSettingsJson()["threshold"]!.GetValue<double>());
    }

    [Fact]
    public void Build_NaNValuesDoNotProduceInvalidJsonNumbers()
    {
        var plan = ObsFilterPlan.Build(Snapshot(gateDb: float.NaN, compRatio: float.PositiveInfinity));

        foreach (ObsFilterSpec spec in plan)
        {
            // ToJsonString бросил бы исключение на NaN/Infinity — это и есть проверка.
            string text = spec.CreateSettingsJson().ToJsonString();
            Assert.False(text.Contains("NaN", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CreateSettingsJson_ReturnsFreshObjectEachCall()
    {
        // JsonNode можно присоединить только к одному родителю — повторное
        // использование одного объекта в двух запросах упало бы в рантайме.
        var spec = ObsFilterPlan.Build(Snapshot())[0];

        JsonObject first = spec.CreateSettingsJson();
        JsonObject second = spec.CreateSettingsJson();

        Assert.False(ReferenceEquals(first, second));
        _ = new JsonObject { ["a"] = first };
        _ = new JsonObject { ["b"] = second };
    }
}
