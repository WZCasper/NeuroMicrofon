using Microsoft.Win32;

namespace NeuroMicrophone.Services;

/// <summary>
/// Включает/выключает автозапуск приложения при входе пользователя в
/// Windows через стандартный раздел реестра
/// HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run.
/// В отличие от установки/удаления драйвера, эта операция НЕ требует
/// прав администратора — раздел находится в пользовательском кусте реестра.
/// </summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NeuroMicrophone";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled, string executablePath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            // Кавычки вокруг пути обязательны на случай пробелов в пути установки
            // (например, "C:\Program Files\NeuroMicrophone\NeuroMicrophone.exe").
            key.SetValue(ValueName, $"\"{executablePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
