using System;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace NeuroMicrophone.Services;

/// <summary>
/// Отслеживает конкретное аудиоустройство (по его ID) через системные
/// уведомления Core Audio (IMMNotificationClient) и сообщает, если оно было
/// физически отключено или переведено в состояние "недоступно"/"отключено".
/// Это и есть требуемая спецификацией "робастная обработка отключения
/// аудиоустройства" — приложение узнаёт о проблеме сразу от ОС, а не
/// только когда WASAPI выбросит исключение при следующей операции чтения/записи.
/// </summary>
public sealed class DeviceChangeNotifier : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly string _watchedDeviceId;
    private bool _disposed;

    /// <summary>Вызывается, когда отслеживаемое устройство пропало или стало недоступно.</summary>
    public event Action<string>? DeviceRemovedOrDisabled;

    public DeviceChangeNotifier(string watchedDeviceId)
    {
        _watchedDeviceId = watchedDeviceId;
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (!string.Equals(deviceId, _watchedDeviceId, StringComparison.OrdinalIgnoreCase)) return;

        if (newState is DeviceState.NotPresent or DeviceState.Unplugged or DeviceState.Disabled)
        {
            DeviceRemovedOrDisabled?.Invoke($"устройство стало недоступно (состояние: {newState}).");
        }
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
        // Не требуется для текущей логики: добавление нового устройства
        // не влияет на уже запущенный движок.
    }

    public void OnDeviceRemoved(string deviceId)
    {
        if (string.Equals(deviceId, _watchedDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            DeviceRemovedOrDisabled?.Invoke("устройство было физически отключено.");
        }
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // Не требуется: приложение работает с явно выбранными устройствами,
        // а не с "устройством по умолчанию".
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // Изменения отдельных свойств устройства (например, громкости в
        // микшере) не являются ошибкой и не требуют реакции.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
        }
        catch (Exception)
        {
            // Enumerator мог быть уже освобождён — не критично на этапе завершения работы.
        }

        _enumerator.Dispose();
    }
}
