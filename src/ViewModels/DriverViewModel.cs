// Этап B разбиения MainViewModel: настоящий дочерний ViewModel для работы с
// виртуальным аудиодрайвером (установка/удаление, состояние "драйвер
// установлен", ссылка на страницу VB-CABLE). В отличие от Этапа A (где это
// был просто ещё один файл того же класса MainViewModel), это отдельный
// класс со своим состоянием — MainViewModel лишь хранит его экземпляр
// в свойстве Driver и подключает к нему два коллбэка родителя (сообщить
// статус, попросить сохранить настройки), не раскрывая ему ничего другого.

using System;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using NeuroMicrophone.Driver;

namespace NeuroMicrophone.ViewModels;

public sealed class DriverViewModel : ViewModelBase
{
    private const string DriverInfFileName = "NeuroMicCable.inf";

    private readonly DriverInstaller _driverInstaller;
    private readonly Action<string?> _setStatusMessage;
    private readonly Action _scheduleSettingsSave;

    private string? _publishedDriverInfName;

    private bool _isDriverInstalled;
    public bool IsDriverInstalled { get => _isDriverInstalled; private set => SetProperty(ref _isDriverInstalled, value); }

    public ICommand OpenVbCableLinkCommand { get; }
    public ICommand InstallDriverCommand { get; }
    public ICommand UninstallDriverCommand { get; }

    public DriverViewModel(DriverInstaller driverInstaller, Action<string?> setStatusMessage, Action scheduleSettingsSave)
    {
        _driverInstaller = driverInstaller;
        _setStatusMessage = setStatusMessage;
        _scheduleSettingsSave = scheduleSettingsSave;

        OpenVbCableLinkCommand = new RelayCommand(() =>
            Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true }));
        InstallDriverCommand = new RelayCommand(async () => await InstallDriverAsync(), () => !IsDriverInstalled);
        UninstallDriverCommand = new RelayCommand(async () => await UninstallDriverAsync(), () => IsDriverInstalled);

        IsDriverInstalled = _driverInstaller.IsDriverInstalled();
    }

    /// <summary>Имя .inf-файла для сохранения в настройки (см. MainViewModel.Settings.cs).</summary>
    public string? PublishedDriverInfName => _publishedDriverInfName;

    /// <summary>
    /// Вызывается из MainViewModel.LoadSettingsAndApplyAsync при загрузке
    /// ранее сохранённого имени опубликованного .inf-файла — на момент
    /// создания DriverViewModel настройки ещё не загружены (загрузка
    /// асинхронная и идёт уже после конструктора), поэтому значение
    /// приходит отдельным вызовом, а не через конструктор.
    /// </summary>
    public void LoadPublishedDriverInfName(string? publishedDriverInfName)
    {
        _publishedDriverInfName = publishedDriverInfName;
    }

    private async Task InstallDriverAsync()
    {
        string infPath = Path.Combine(AppContext.BaseDirectory, "Driver", "Package", DriverInfFileName);
        DriverInstallResult result = await _driverInstaller.InstallDriverAsync(infPath);

        if (result.IsSuccess)
        {
            _publishedDriverInfName = result.PublishedInfName;
            IsDriverInstalled = _driverInstaller.IsDriverInstalled();
            _setStatusMessage(null);
            _scheduleSettingsSave();
        }
        else
        {
            _setStatusMessage(result.Message);
        }
    }

    private async Task UninstallDriverAsync()
    {
        DriverInstallResult result = await _driverInstaller.UninstallDriverAsync(_publishedDriverInfName);

        if (result.IsSuccess)
        {
            _publishedDriverInfName = null;
            IsDriverInstalled = _driverInstaller.IsDriverInstalled();
            _scheduleSettingsSave();
        }
        else
        {
            _setStatusMessage(result.Message);
        }
    }
}
