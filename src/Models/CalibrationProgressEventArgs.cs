using System;

namespace NeuroMicrophone.Models;

/// <summary>
/// Снимок прогресса калибровки, передаваемый через IProgress&lt;T&gt;
/// из фонового потока калибровки в поток UI (маршалинг делает сам
/// Progress&lt;T&gt;, вызывая обработчик через SynchronizationContext,
/// с которым он был создан — то есть в потоке UI).
/// </summary>
public sealed class CalibrationProgressEventArgs : EventArgs
{
    /// <summary>Номер текущего этапа: 1, 2 или 3.</summary>
    public int StepNumber { get; }

    /// <summary>Текст инструкции для пользователя на этом этапе.</summary>
    public string Instruction { get; }

    /// <summary>Доля выполнения текущего этапа, от 0.0 до 1.0.</summary>
    public double PhaseFraction { get; }

    /// <summary>Доля выполнения всей калибровки (все 3 этапа), от 0.0 до 1.0.</summary>
    public double OverallFraction { get; }

    public CalibrationProgressEventArgs(int stepNumber, string instruction, double phaseFraction, double overallFraction)
    {
        StepNumber = stepNumber;
        Instruction = instruction;
        PhaseFraction = phaseFraction;
        OverallFraction = overallFraction;
    }
}
