using System.Collections.Generic;

namespace NeuroMicrophone.Models;

/// <summary>
/// Именованный набор параметров DSP-цепочки, который можно применить одним
/// действием — быстрая отправная точка вместо (или вместе с) автокалибровкой.
/// Потолок лимитера сюда намеренно не входит: -2 dBFS — это требование
/// "никогда не допускать клиппинга", а не вопрос вкуса, поэтому он остаётся
/// фиксированным независимо от выбранного пресета.
/// </summary>
public sealed class DspPreset
{
    public string Name { get; }
    public float GateThresholdDb { get; }
    public float DenoiserWetMix { get; }
    public float AgcTargetLevelDb { get; }
    public float CompressorThresholdDb { get; }
    public float CompressorRatio { get; }

    public DspPreset(
        string name,
        float gateThresholdDb,
        float denoiserWetMix,
        float agcTargetLevelDb,
        float compressorThresholdDb,
        float compressorRatio)
    {
        Name = name;
        GateThresholdDb = gateThresholdDb;
        DenoiserWetMix = denoiserWetMix;
        AgcTargetLevelDb = agcTargetLevelDb;
        CompressorThresholdDb = compressorThresholdDb;
        CompressorRatio = compressorRatio;
    }

    public override string ToString() => Name;

    public static IReadOnlyList<DspPreset> BuiltIn { get; } = new List<DspPreset>
    {
        new("Тихая комната",
            gateThresholdDb: -55f, denoiserWetMix: 0.5f,
            agcTargetLevelDb: -18f, compressorThresholdDb: -14f, compressorRatio: 3f),

        new("Шумный опенспейс",
            gateThresholdDb: -38f, denoiserWetMix: 1.0f,
            agcTargetLevelDb: -18f, compressorThresholdDb: -12f, compressorRatio: 4f),

        new("Стрим",
            gateThresholdDb: -45f, denoiserWetMix: 0.75f,
            agcTargetLevelDb: -16f, compressorThresholdDb: -10f, compressorRatio: 3.5f),
    };
}
