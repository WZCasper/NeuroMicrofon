using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
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
    /// даже если IsSuccess == true (например, если WMI ещё не успел
    /// проиндексировать устройство) — в этом случае используйте
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
/// системе и умеет (при корректно подписанном INF-пакете) установить его
/// через штатную системную утилиту pnputil.exe.
///
/// Само приложение НЕ требует прав администратора для запуска (см.
/// app.manifest — level="asInvoker"): администратор нужен только для
/// самих операций pnputil, поэтому повышение прав запрашивается точечно,
/// непосредственно на время конкретного вызова InstallDriverAsync /
/// UninstallDriverAsync (через ShellExecute с verb="runas"), а не для
/// всего приложения сразу.
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
    /// Запрашивает повышение прав (UAC) точечно, только на время этого вызова.
    /// </summary>
    public async Task<DriverInstallResult> InstallDriverAsync(string infPath)
    {
        if (!File.Exists(infPath))
        {
            return DriverInstallResult.Failure($"INF-файл не найден: {infPath}");
        }

        try
        {
            (int exitCode, string output) = await RunElevatedCommandAsync(
                $"pnputil.exe /add-driver \"{infPath}\" /install").ConfigureAwait(false);

            if (exitCode == 0)
            {
                string? publishedName = await FindPublishedDriverInfNameAsync().ConfigureAwait(false);
                return DriverInstallResult.Success(output, publishedName);
            }

            return DriverInstallResult.Failure(
                $"pnputil завершился с кодом {exitCode}. Наиболее вероятная причина — " +
                $"INF-пакет не подписан действительным сертификатом (см. README.md). Вывод: {output}");
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
    /// Windows для устройства VirtualDeviceName через WMI (Win32_PnPSignedDriver).
    /// Это операция чтения — прав администратора не требует, поэтому
    /// выполняется напрямую, без запроса повышения.
    ///
    /// Раньше это делалось разбором текста "pnputil /enum-drivers" по
    /// английским подписям "Published Name" / "Original Name" — на
    /// локализованной (например, русской) Windows эти подписи выводятся на
    /// другом языке, и разбор молча не находил совпадений. WMI возвращает
    /// значения полей программно, независимо от языка интерфейса ОС, и
    /// заодно избавляет от необходимости отдельно передавать исходное имя
    /// INF-файла: ищем сразу по знакомому имени устройства.
    /// LIKE, а не точное совпадение, — Windows на некоторых системах
    /// добавляет к имени устройства дополнительный суффикс в скобках.
    /// </summary>
    public async Task<string?> FindPublishedDriverInfNameAsync()
    {
        try
        {
            return await Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT InfName FROM Win32_PnPSignedDriver WHERE DeviceName LIKE '%{VirtualDeviceName}%'");

                foreach (ManagementObject device in searcher.Get())
                {
                    using (device)
                    {
                        if (device["InfName"] is string infName && !string.IsNullOrWhiteSpace(infName))
                        {
                            return infName;
                        }
                    }
                }

                return null;
            }).ConfigureAwait(false);
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
    /// определить его сам через FindPublishedDriverInfNameAsync (по WMI,
    /// без необходимости знать исходное имя INF-файла). Запрашивает
    /// повышение прав точечно, только на время этого вызова.
    /// </summary>
    public async Task<DriverInstallResult> UninstallDriverAsync(string? publishedInfName = null)
    {
        if (string.IsNullOrWhiteSpace(publishedInfName))
        {
            publishedInfName = await FindPublishedDriverInfNameAsync().ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(publishedInfName))
        {
            return DriverInstallResult.Failure(
                "Не удалось определить опубликованное имя драйвера (oemNN.inf) для удаления. " +
                "Посмотрите его вручную через 'pnputil /enum-drivers' и передайте явно.");
        }

        try
        {
            (int exitCode, string output) = await RunElevatedCommandAsync(
                $"pnputil.exe /delete-driver \"{publishedInfName}\" /uninstall /force").ConfigureAwait(false);

            return exitCode == 0
                ? DriverInstallResult.Success(output)
                : DriverInstallResult.Failure($"pnputil завершился с кодом {exitCode}. Вывод: {output}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return DriverInstallResult.Failure("Удаление отменено пользователем (запрос UAC отклонён).");
        }
        catch (Exception ex)
        {
            return DriverInstallResult.Failure($"Ошибка удаления драйвера: {ex.Message}");
        }
    }

    /// <summary>
    /// Запускает команду через cmd.exe с точечным запросом повышения прав
    /// (UAC) — только на время этого вызова, не затрагивая остальное
    /// приложение. ShellExecute (обязательный для запроса UAC через
    /// verb="runas") не поддерживает прямое перенаправление stdout/stderr
    /// в текущий процесс, поэтому вывод команды перенаправляется во
    /// временный файл, который затем читает уже неповышенный родительский
    /// процесс — тот же приём, что используется в Installer/setup_script.iss.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunElevatedCommandAsync(string commandLine)
    {
        string tempOutputFile = Path.Combine(Path.GetTempPath(), $"nm_pnputil_{Guid.NewGuid():N}.txt");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {commandLine} > \"{tempOutputFile}\" 2>&1",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using Process? process = Process.Start(startInfo);
            if (process == null)
            {
                return (-1, "Не удалось запустить процесс с повышенными правами.");
            }

            await process.WaitForExitAsync().ConfigureAwait(false);

            string output = File.Exists(tempOutputFile)
                ? await File.ReadAllTextAsync(tempOutputFile).ConfigureAwait(false)
                : string.Empty;

            return (process.ExitCode, output);
        }
        finally
        {
            try
            {
                if (File.Exists(tempOutputFile)) File.Delete(tempOutputFile);
            }
            catch (Exception)
            {
                // Временный файл не критичен для результата операции.
            }
        }
    }
}
