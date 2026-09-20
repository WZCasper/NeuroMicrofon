using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using NeuroMicrophone.Models;

namespace NeuroMicrophone.Services;

/// <summary>
/// Читает и пишет AppSettings в
/// %LocalAppData%\NeuroMicrophone\settings.json через System.Text.Json
/// (входит в состав .NET, дополнительных NuGet-пакетов не требует).
/// Ошибки чтения/записи не приводят к падению приложения — при сбое
/// LoadAsync просто возвращает null (используются значения по умолчанию),
/// а SaveAsync молча возвращает false, чтобы не мешать пользователю
/// всплывающими сообщениями об ошибках сохранения фоновых настроек.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _settingsFilePath;

    public SettingsService()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NeuroMicrophone");

        Directory.CreateDirectory(directory);
        _settingsFilePath = Path.Combine(directory, "settings.json");
    }

    public async Task<AppSettings?> LoadAsync()
    {
        try
        {
            if (!File.Exists(_settingsFilePath)) return null;

            // ConfigureAwait(false) нужен и на самой операции, и на неявном
            // await, который "await using" вызывает при выходе из блока
            // (DisposeAsync) — иначе гарантия "эта цепочка никогда не
            // пытается вернуться в поток UI" была бы неполной.
            FileStream stream = File.OpenRead(_settingsFilePath);
            await using ConfiguredAsyncDisposable _ = stream.ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Повреждённый или недоступный файл настроек — работаем со значениями по умолчанию.
            return null;
        }
    }

    public async Task<bool> SaveAsync(AppSettings settings)
    {
        try
        {
            FileStream stream = File.Create(_settingsFilePath);
            await using ConfiguredAsyncDisposable _ = stream.ConfigureAwait(false);
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
