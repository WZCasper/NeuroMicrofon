// Дочерний ViewModel карточки "Интеграция с OBS": параметры подключения к
// встроенному в OBS серверу WebSocket, список аудиоисточников OBS, выбор
// источника-микрофона и кнопка APPLY, которая переносит текущие настройки
// DSP-цепочки NeuroMicrophone на фильтры выбранного источника в OBS.
//
// Как и остальные дочерние ViewModel, получает от MainViewModel только то,
// что ему реально нужно: способ снять "снимок" текущих настроек DSP (делегат,
// а не ссылка на DspViewModel) и коллбэк "попросить сохранить настройки".
//
// Пароль OBS сознательно хранится только в памяти и на диск не пишется:
// шифровать его без дополнительных пакетов нечем, а хранить пароль к
// стороннему сервису открытым текстом в settings.json — плохая практика.

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using NeuroMicrophone.Obs;

namespace NeuroMicrophone.ViewModels;

public sealed class ObsViewModel : ViewModelBase
{
    private readonly ObsService _service;
    private readonly Func<ObsDspSnapshot> _getDspSnapshot;
    private readonly Action _scheduleSettingsSave;

    private string _host = ObsConnectionSettings.DefaultHost;
    public string Host
    {
        get => _host;
        set
        {
            if (!SetProperty(ref _host, value ?? string.Empty)) return;
            _scheduleSettingsSave();
        }
    }

    private string _portText = ObsConnectionSettings.DefaultPort.ToString(CultureInfo.InvariantCulture);
    public string PortText
    {
        get => _portText;
        set
        {
            if (!SetProperty(ref _portText, value ?? string.Empty)) return;
            _scheduleSettingsSave();
        }
    }

    // Пароль не сохраняется (см. комментарий в начале файла) и не имеет
    // привязки в XAML: PasswordBox не поддерживает привязку к Password, поэтому
    // MainWindow передаёт значение сюда из события PasswordChanged.
    public string Password { get; set; } = string.Empty;

    public ObservableCollection<ObsInputInfo> Sources { get; } = new();

    private ObsInputInfo? _selectedSource;
    public ObsInputInfo? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value)) return;
            _scheduleSettingsSave();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string? _statusText;
    public string? StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private bool _isStatusError;
    public bool IsStatusError { get => _isStatusError; private set => SetProperty(ref _isStatusError, value); }

    private bool _isStatusSuccess;
    public bool IsStatusSuccess { get => _isStatusSuccess; private set => SetProperty(ref _isStatusSuccess, value); }

    public ICommand RefreshSourcesCommand { get; }
    public ICommand ApplyCommand { get; }

    public ObsViewModel(ObsService service, Func<ObsDspSnapshot> getDspSnapshot, Action scheduleSettingsSave)
    {
        _service = service;
        _getDspSnapshot = getDspSnapshot;
        _scheduleSettingsSave = scheduleSettingsSave;

        RefreshSourcesCommand = new RelayCommand(async () => await RefreshSourcesAsync(), () => !IsBusy);
        ApplyCommand = new RelayCommand(async () => await ApplyAsync(), () => !IsBusy && SelectedSource != null);
    }

    // --- Сохранение/загрузка (вызываются из MainViewModel.Settings.cs) ---

    /// <summary>Порт для сохранения в настройки: введённое число, если оно корректно, иначе значение по умолчанию.</summary>
    public int PortForSettings => TryParsePort(PortText, out int port) ? port : ObsConnectionSettings.DefaultPort;

    public string? SelectedSourceNameForSettings => SelectedSource?.Name;

    /// <summary>
    /// Применяет сохранённые параметры. Выбранный источник до первого
    /// обновления списка показывается как единственный элемент с этим именем
    /// — чтобы кнопка APPLY работала сразу после запуска программы, не
    /// заставляя пользователя каждый раз нажимать "Обновить".
    /// </summary>
    public void LoadSettings(string? host, int port, string? sourceName)
    {
        if (!string.IsNullOrWhiteSpace(host)) _host = host;
        if (port is >= 1 and <= 65535) _portText = port.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(Host));
        OnPropertyChanged(nameof(PortText));

        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            var remembered = new ObsInputInfo(sourceName, string.Empty);
            Sources.Clear();
            Sources.Add(remembered);
            SelectedSource = remembered;
        }
    }

    // --- Действия ---

    private async Task RefreshSourcesAsync()
    {
        if (!TryGetConnectionSettings(out ObsConnectionSettings settings)) return;

        // Имя запоминаем ДО очистки списка: ComboBox при Clear() обнуляет SelectedSource через привязку.
        string? previousName = SelectedSource?.Name;

        IsBusy = true;
        SetStatus("Подключение к OBS…", isError: false, isSuccess: false);
        try
        {
            var inputs = await _service.ListAudioInputsAsync(settings, CancellationToken.None);

            Sources.Clear();
            foreach (ObsInputInfo input in inputs) Sources.Add(input);

            ObsInputInfo? restored = previousName == null ? null : Sources.FirstOrDefault(s => s.Name == previousName);
            SelectedSource = restored ?? (Sources.Count == 1 ? Sources[0] : null);

            if (Sources.Count == 0)
            {
                SetStatus("В OBS нет аудиоисточников. Добавьте источник «Захват входного аудиоустройства» " +
                          "или используйте встроенный «Микрофон/доп. аудио», затем нажмите «ОБНОВИТЬ».", isError: true, isSuccess: false);
            }
            else if (previousName != null && restored == null)
            {
                SetStatus($"Источник «{previousName}» в OBS не найден. Выберите микрофон из списка.", isError: true, isSuccess: false);
            }
            else
            {
                SetStatus($"Подключено к OBS. Найдено аудиоисточников: {Sources.Count}. Выберите микрофон и нажмите APPLY.", isError: false, isSuccess: true);
            }
        }
        catch (ObsException ex)
        {
            SetStatus(ex.Message, isError: true, isSuccess: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyAsync()
    {
        ObsInputInfo? source = SelectedSource;
        if (source == null)
        {
            SetStatus("Сначала выберите микрофон (источник) в OBS.", isError: true, isSuccess: false);
            return;
        }

        if (!TryGetConnectionSettings(out ObsConnectionSettings settings)) return;

        // Снимок берём здесь, в потоке UI, до любых await: ViewModel DSP нельзя читать из фонового потока.
        ObsDspSnapshot snapshot = _getDspSnapshot();

        IsBusy = true;
        SetStatus("Подключение к OBS…", isError: false, isSuccess: false);
        try
        {
            ObsApplyResult result = await _service.ApplyAsync(settings, source.Name, ObsFilterPlan.Build(snapshot), CancellationToken.None);
            SetStatus(BuildSuccessMessage(result), isError: false, isSuccess: true);
        }
        catch (ObsException ex)
        {
            SetStatus(ex.Message, isError: true, isSuccess: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string BuildSuccessMessage(ObsApplyResult result)
    {
        var parts = new System.Collections.Generic.List<string>
        {
            $"Применено к «{result.SourceName}» (OBS {result.ObsVersion}).",
        };

        if (result.Created.Count > 0) parts.Add($"Добавлены фильтры: {string.Join(", ", result.Created)}.");
        if (result.Updated.Count > 0) parts.Add($"Обновлены: {string.Join(", ", result.Updated)}.");

        // Честно о границах переноса: у OBS нет аналогов этих модулей.
        parts.Add("Автоусиление (AGC), срез низких частот и «сила» шумоподавления в OBS аналога не имеют — не переносятся.");
        parts.AddRange(result.Warnings);

        return string.Join(" ", parts);
    }

    private bool TryGetConnectionSettings(out ObsConnectionSettings settings)
    {
        settings = null!;

        string host = Host.Trim();
        if (host.Length == 0)
        {
            SetStatus("Укажите адрес OBS (обычно 127.0.0.1 — это тот же компьютер).", isError: true, isSuccess: false);
            return false;
        }

        if (!TryParsePort(PortText, out int port))
        {
            SetStatus("Порт должен быть числом от 1 до 65535 (по умолчанию в OBS — 4455).", isError: true, isSuccess: false);
            return false;
        }

        settings = new ObsConnectionSettings(host, port, string.IsNullOrEmpty(Password) ? null : Password);
        return true;
    }

    private static bool TryParsePort(string text, out int port)
    {
        return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
    }

    private void SetStatus(string? text, bool isError, bool isSuccess)
    {
        StatusText = text;
        IsStatusError = isError;
        IsStatusSuccess = isSuccess;
    }
}
