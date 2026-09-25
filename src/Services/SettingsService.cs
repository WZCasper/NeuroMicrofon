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
        // Пишем во временный файл и только затем атомарно заменяем им
        // settings.json — File.Move с overwrite: true в пределах одного
        // тома выполняется как атомарное переименование на уровне файловой
        // системы. Если приложение упадёт или пропадёт питание посреди
        // записи, settings.json либо останется прежним, либо станет новым,
        // но никогда не окажется наполовину записанным и повреждённым
        // (раньше запись шла прямо в settings.json через File.Create,
        // что не давало такой гарантии).
        string tempFilePath = _settingsFilePath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            FileStream stream = File.Create(tempFilePath);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions).ConfigureAwait(false);
            }

            // К этому моменту поток уже закрыт и сброшен на диск — можно
            // безопасно переименовывать/заменять итоговый файл.
            File.Move(tempFilePath, _settingsFilePath, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            // Если Move не выполнился (или сериализация упала раньше) —
            // не оставляем временный файл висеть в папке настроек.
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
            catch (Exception)
            {
                // Не критично — не мешаем основному результату SaveAsync.
            }
        }
    }
}
