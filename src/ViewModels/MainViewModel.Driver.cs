// MainViewModel: работа с виртуальным аудиодрайвером (установка/удаление,
// состояние "драйвер установлен", ссылка на страницу VB-CABLE). Вынесено из
// основного файла по мере роста MainViewModel — здесь только код, имеющий
// отношение к драйверу как таковому, а не к DSP-цепочке или калибровке.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using NeuroMicrophone.Audio;
using NeuroMicrophone.Driver;
using NeuroMicrophone.Models;
using NeuroMicrophone.Services;

namespace NeuroMicrophone.ViewModels;

public partial class MainViewModel
{
    private const string DriverInfFileName = "NeuroMicCable.inf";

    private readonly DriverInstaller _driverInstaller = new();

    private string? _publishedDriverInfName;

    private bool _isDriverInstalled;
    public bool IsDriverInstalled { get => _isDriverInstalled; private set => SetProperty(ref _isDriverInstalled, value); }

    public ICommand OpenVbCableLinkCommand { get; }

    public ICommand InstallDriverCommand { get; }
    public ICommand UninstallDriverCommand { get; }

    private async Task InstallDriverAsync()
    {
        string infPath = Path.Combine(AppContext.BaseDirectory, "Driver", "Package", DriverInfFileName);
        DriverInstallResult result = await _driverInstaller.InstallDriverAsync(infPath);

        if (result.IsSuccess)
        {
            _publishedDriverInfName = result.PublishedInfName;
            IsDriverInstalled = _driverInstaller.IsDriverInstalled();
            StatusMessage = null;
            ScheduleSettingsSave();
        }
        else
        {
            StatusMessage = result.Message;
        }
    }

    private async Task UninstallDriverAsync()
    {
        DriverInstallResult result = await _driverInstaller.UninstallDriverAsync(_publishedDriverInfName);

        if (result.IsSuccess)
        {
            _publishedDriverInfName = null;
            IsDriverInstalled = _driverInstaller.IsDriverInstalled();
            ScheduleSettingsSave();
        }
        else
        {
            StatusMessage = result.Message;
        }
    }
}
