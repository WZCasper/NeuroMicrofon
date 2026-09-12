using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NeuroMicrophone.Services;

/// <summary>
/// Регистрирует системную глобальную горячую клавишу через Win32
/// RegisterHotKey/UnregisterHotKey и перехватывает сообщение WM_HOTKEY через
/// хук окна (HwndSource). Это самый простой и надёжный способ для
/// WPF-приложения принимать нажатия клавиш, даже когда окно свёрнуто в
/// трей или не в фокусе.
///
/// Комбинация задаётся при создании и может быть заменена в любой момент
/// через ChangeHotkey (например, когда пользователь выбирает другой
/// вариант в настройках) — это просто перерегистрация с новыми параметрами.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4E4D; // произвольный уникальный идентификатор в пределах приложения ("NM")

    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    /// <summary>Вызывается в потоке UI при нажатии зарегистрированной комбинации.</summary>
    public event Action? HotkeyPressed;

    /// <summary>true, если текущая комбинация успешно зарегистрирована в системе.</summary>
    public bool IsRegistered => _registered;

    /// <param name="window">Окно, к дескриптору которого будет привязан хук сообщений.</param>
    /// <param name="modifiers">Флаги модификаторов (см. Models.HotkeyModifiers).</param>
    /// <param name="virtualKey">Виртуальный код клавиши (например, 0x4D для 'M').</param>
    public HotkeyService(Window window, uint modifiers, uint virtualKey)
    {
        var helper = new WindowInteropHelper(window);
        IntPtr handle = helper.EnsureHandle();

        _source = HwndSource.FromHwnd(handle)
                  ?? throw new InvalidOperationException("Не удалось получить HwndSource для регистрации горячей клавиши.");

        _source.AddHook(WndProc);
        _registered = RegisterHotKey(_source.Handle, HotkeyId, modifiers, virtualKey);
    }

    /// <summary>
    /// Снимает текущую регистрацию (если была) и регистрирует новую
    /// комбинацию. Возвращает false, если новая комбинация уже занята
    /// другой программой в системе (например, если выбранная замена сама
    /// оказалась конфликтной) — в этом случае горячая клавиша просто не
    /// сработает, но приложение продолжает работать в остальном штатно.
    /// </summary>
    public bool ChangeHotkey(uint modifiers, uint virtualKey)
    {
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }

        _registered = RegisterHotKey(_source.Handle, HotkeyId, modifiers, virtualKey);
        return _registered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_registered)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }

        _source.RemoveHook(WndProc);
    }
}
