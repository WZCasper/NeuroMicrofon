namespace NeuroMicrophone.Obs;

/// <summary>Параметры подключения к встроенному в OBS серверу obs-websocket (OBS 28+).</summary>
public sealed record ObsConnectionSettings(string Host, int Port, string? Password)
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 4455;
}
