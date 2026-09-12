using NeuroMicrophone.ViewModels;
using Xunit;

namespace NeuroMicrophone.Tests;

/// <summary>
/// "Дымовой" тест: просто создаёт MainViewModel и проверяет, что
/// конструктор не бросает исключение.
///
/// Добавлен после реального сбоя в проде: NullReferenceException в
/// конструкторе (обращение к полю _saveDebounceTimer до его создания, из
/// сеттеров SelectedInputDevice/SelectedOutputDevice/SelectedMonitorDevice,
/// вызываемых раньше по коду) оборачивался механизмом WPF/рефлексии в
/// TargetInvocationException с общим текстом "Exception has been thrown by
/// the target of an invocation." — из-за чего пользователь не мог понять
/// причину, а обычные юнит-тесты DSP-классов этот класс ошибок не ловят,
/// поскольку не создают MainViewModel вообще.
///
/// Тест не проверяет конкретную функциональность — только то, что
/// приложение вообще способно дойти до отображения главного окна.
/// </summary>
public class MainViewModelSmokeTests
{
    [Fact]
    public void Constructor_DoesNotThrow()
    {
        using var viewModel = new MainViewModel();
        Assert.NotNull(viewModel);
    }
}
