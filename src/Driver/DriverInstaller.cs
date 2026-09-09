using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace NeuroMicrophone.Driver;

/// <summary>
/// Результат операции установки/удаления драйвера.
/// </summary>
public sealed class DriverInstallResult
{
    public bool IsSuccess { get; }
    public string Message { get; }

    /// <summary>
    /// "Опубликованное" имя INF в хранилище драйверов (например, "oem12.inf"),
    /// если его удалось определить сразу после установки. Может быть null,
    /// даже если IsSuccess == true (например, если формат вывода pnputil
    /// отличался от ожидаемого) — в этом случае используйте
    /// DriverInstaller.FindPublishedDriverInfNameAsync позже.
    /// </summary>
    public string? PublishedInfName { get; }

    private DriverInstallResult(bool isSuccess, string message, string? publishedInfName = null)
    {
        IsSuccess = isSuccess;
        Message = message;
        PublishedInfName = publishedInfName;
    }

    public static DriverInstallResult Success(string message, string? publishedInfName = null) => new(true, message, publishedInfName);
    public static DriverInstallResult Failure(string message) => new(false, message);
}

/// <summary>
/// Проверяет наличие виртуального устройства "NeuroMicrophone Cable" в
/// системе и умеет (при наличии прав администратора и корректно подписанного
/// INF-пакета) установить его через штатную системную утилиту pnputil.exe.
///
/// ВАЖНО: начиная с Windows 10, ядро проверяет цифровую подпись драйверов
/// (Driver Signature Enforcement). Эта проверка выполняется самой ОС и
/// не может быть обойдена никаким кодом на уровне приложения — INF/SYS/CAT
/// файлы в переданном пакете обязаны быть подписаны действительным
/// сертификатом (собственная EV-подпись + аттестация в Microsoft Hardware
/// Dev Center, либо уже подписанный переиздаваемый пакет стороннего
/// поставщика виртуального аудиокабеля). Подробности — в README.md.
/// </summary>
public sealed class DriverInstaller
{
    public const string VirtualDeviceName = "NeuroMicrophone Cable";

    /// <summary>
    /// Проверяет, присутствует ли в системе (в любом состоянии — активном,
    /// отключённом и т.п.) устройство вывода с именем VirtualDeviceName.
    /// </summary>
    public bool IsDriverInstalled()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All))
        {
            using (device)
            {
                if (device.FriendlyName.Contains(VirtualDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Возвращает MMDevice виртуального устройства, если оно активно, иначе null.
    /// Вызывающий код обязан вызвать Dispose() на возвращённом объекте.
    /// </summary>
    public MMDevice? FindActiveVirtualDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains(VirtualDeviceName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Устанавливает драйвер из указанного INF-файла через pnputil.exe.
    /// Требует запуска процесса с правами администратора (см. app.manifest).
    /// </summary>
    public async Task<DriverInstallResult> InstallDriverAsync(string infPath)
    {
        if (!File.Exists(infPath))
        {
            return DriverInstallResult.Failure($"INF-файл не найден: {infPath}");
        }

        if (!IsRunningAsAdministrator())
        {
            return DriverInstallResult.Failure("Требуются права администратора для установки драйвера. Перезапустите приложение от имени администратора.");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/add-driver \"{infPath}\" /install",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return DriverInstallResult.Failure("Не удалось запустить pnputil.exe.");
            }

            string standardOutput = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string standardError = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                string? publishedName = await FindPublishedDriverInfNameAsync(Path.GetFileName(infPath)).ConfigureAwait(false);
                return DriverInstallResult.Success(standardOutput, publishedName);
            }

            return DriverInstallResult.Failure(
                $"pnputil завершился с кодом {process.ExitCode}. Наиболее вероятная причина — " +
                $"INF-пакет не подписан действительным сертификатом (см. README.md). Вывод: {standardError}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — пользователь отклонил запрос UAC.
            return DriverInstallResult.Failure("Установка отменена пользователем (запрос UAC отклонён).");
        }
        catch (Exception ex)
        {
            return DriverInstallResult.Failure($"Ошибка установки драйвера: {ex.Message}");
        }
    }

    /// <summary>
    /// Ищет "опубликованное" имя INF (вида oemNN.inf) в хранилище драйверов
    /// Windows по исходному имени файла драйвера, разбирая вывод
    /// "pnputil /enum-drivers". Этот способ надёжнее, чем парсинг вывода
    /// команды установки: его можно вызвать в любой момент — даже спустя
    /// долгое время после установки, — а не только сразу после неё.
    ///
    /// Примечание: текст вывода pnputil локализован под язык интерфейса
    /// Windows, поэтому разбор ориентируется на английские подписи
    /// "Published Name" / "Original Name", которые используются на
    /// англоязычных системах. На локализованных системах имена полей вывода
    /// могут отличаться — в этом случае метод вернёт null, и стоит либо
    /// сохранять имя во время установки (см. InstallDriverAsync), либо
    /// определить локализованные подписи для конкретного языка.
    /// </summary>
    public async Task<string?> FindPublishedDriverInfNameAsync(string originalInfFileName)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/enum-drivers",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            // Записи в выводе pnputil /enum-drivers разделены пустыми строками —
            // разбиваем на блоки и ищем блок, "Original Name" которого совпадает
            // с исходным именем нашего INF-файла.
            string[] blocks = Regex.Split(output, @"(?:\r?\n){2,}");

            foreach (string block in blocks)
            {
                Match originalMatch = Regex.Match(block, @"Original Name\s*:\s*(\S+)", RegexOptions.IgnoreCase);
                if (!originalMatch.Success) continue;

                if (!string.Equals(originalMatch.Groups[1].Value, originalInfFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Match publishedMatch = Regex.Match(block, @"Published Name\s*:\s*(\S+)", RegexOptions.IgnoreCase);
                if (publishedMatch.Success)
                {
                    return publishedMatch.Groups[1].Value;
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Удаляет драйвер. pnputil ожидает "опубликованное" имя INF в
    /// хранилище драйверов (вида oemNN.inf), а не исходное имя файла.
    /// Если publishedInfName не передан, метод сначала попробует
    /// определить его сам через FindPublishedDriverInfNameAsync, используя
    /// originalInfFileNameForLookup.
    /// </summary>
    public async Task<DriverInstallResult> UninstallDriverAsync(string? publishedInfName, string? originalInfFileNameForLookup = null)
    {
        if (!IsRunningAsAdministrator())
        {
            return DriverInstallResult.Failure("Требуются права администратора для удаления драйвера.");
        }

        if (string.IsNullOrWhiteSpace(publishedInfName) && !string.IsNullOrWhiteSpace(originalInfFileNameForLookup))
        {
            publishedInfName = await FindPublishedDriverInfNameAsync(originalInfFileNameForLookup).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(publishedInfName))
        {
            return DriverInstallResult.Failure(
                "Не удалось определить опубликованное имя драйвера (oemNN.inf) для удаления. " +
                "Посмотрите его вручную через 'pnputil /enum-drivers' и передайте явно.");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/delete-driver \"{publishedInfName}\" /uninstall /force",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return DriverInstallResult.Failure("Не удалось запустить pnputil.exe.");
            }

            string standardOutput = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            return process.ExitCode == 0
                ? DriverInstallResult.Success(standardOutput)
                : DriverInstallResult.Failure($"pnputil завершился с кодом {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            return DriverInstallResult.Failure($"Ошибка удаления драйвера: {ex.Message}");
        }
    }

    public static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
