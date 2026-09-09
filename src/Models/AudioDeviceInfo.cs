namespace NeuroMicrophone.Models;

/// <summary>
/// Лёгкое представление аудиоустройства для привязки к ComboBox в UI,
/// не тянущее за собой весь COM-объект MMDevice (который должен
/// корректно освобождаться и не должен "жить" дольше enumerator'а).
/// </summary>
public sealed class AudioDeviceInfo
{
    public string Id { get; }
    public string Name { get; }

    public AudioDeviceInfo(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public override string ToString() => Name;

    public override bool Equals(object? obj) => obj is AudioDeviceInfo other && Id == other.Id;

    public override int GetHashCode() => Id.GetHashCode();
}
