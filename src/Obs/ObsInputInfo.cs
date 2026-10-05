namespace NeuroMicrophone.Obs;

/// <summary>
/// Аудиоисточник OBS (вход), на который можно повесить фильтры. Равенство —
/// по значению (Name + Kind), поэтому ComboBox корректно находит выбранный
/// элемент после обновления списка с сервера.
/// </summary>
public sealed record ObsInputInfo(string Name, string Kind)
{
    /// <summary>
    /// Источники "Захват входного аудиоустройства" (микрофоны) — в списке
    /// показываются выше остальных, потому что именно к ним чаще всего
    /// нужно применить настройки микрофона.
    /// </summary>
    public bool IsMicrophoneLike => Kind.Contains("input_capture", System.StringComparison.OrdinalIgnoreCase);

    // ComboBox показывает ToString() элемента — как и AudioDeviceInfo в этом же проекте.
    public override string ToString() => Name;
}

/// <summary>Итог применения настроек к источнику OBS — для понятного отчёта пользователю.</summary>
public sealed record ObsApplyResult(
    string SourceName,
    string ObsVersion,
    System.Collections.Generic.IReadOnlyList<string> Created,
    System.Collections.Generic.IReadOnlyList<string> Updated,
    System.Collections.Generic.IReadOnlyList<string> Warnings);
