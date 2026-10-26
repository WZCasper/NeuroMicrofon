using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;

namespace NeuroMicrophone.Services;

/// <summary>
/// Управляет значком в области уведомлений (System Tray) через
/// System.Windows.Forms.NotifyIcon — стандартный, надёжный способ для
/// WPF-приложений (требует UseWindowsForms=true в csproj).
///
/// Иконка рисуется программно через GDI+, а не загружается из внешнего
/// файла .ico — это устраняет зависимость от отсутствующего ресурса
/// и гарантирует, что значок всегда на месте.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly NotifyIcon _notifyIcon;
    private readonly Window _window;

    // Bitmap.GetHicon() возвращает неуправляемый хендл GDI (HICON), которым
    // Icon.FromHandle() не завладевает — согласно документации самого
    // GetHicon(), освобождать этот хендл через DestroyIcon обязан вызывающий
    // код. Icon.Dispose() для иконки, созданной через FromHandle, хендл НЕ
    // трогает (ровно для таких случаев, когда хендл мог быть общим/внешним),
    // поэтому без явного DestroyIcon ниже, в Dispose(), хендл оставался бы
    // висеть до завершения процесса целиком.
    private readonly IntPtr _iconHandle;

    /// <summary>Вызывается при выборе пункта "Выход" в контекстном меню трея.</summary>
    public event Action? ExitRequested;

    public TrayIconService(Window window)
    {
        _window = window;

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Показать NeuroMicrophone", null, (_, _) => ShowWindow());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke());

        (Icon icon, IntPtr handle) = CreateTrayIcon();
        _iconHandle = handle;

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Visible = false,
            Text = "NeuroMicrophone",
            ContextMenuStrip = contextMenu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();
    }

    /// <summary>
    /// Показывает значок в трее и прячет окно. showHint — показать ли рядом
    /// короткое всплывающее пояснение (нужно только один раз, пока
    /// пользователь ещё не знает про эту иконку — дальше он уже в курсе).
    /// </summary>
    public void ShowInTray(bool showHint)
    {
        _notifyIcon.Visible = true;
        _window.Hide();

        if (showHint)
        {
            _notifyIcon.ShowBalloonTip(
                5000,
                "NeuroMicrophone свёрнут в трей",
                "Обработка звука и горячая клавиша заглушки продолжают работать в фоне. Чтобы открыть окно — дважды щёлкните по этому значку.",
                ToolTipIcon.Info);
        }
    }

    private void ShowWindow()
    {
        _notifyIcon.Visible = false;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>
    /// Рисует простую пиктограмму микрофона 32x32 средствами GDI+, без
    /// использования внешних файлов ресурсов. Возвращает managed-обёртку
    /// вместе с сырым хендлом — он нужен отдельно, т.к. сама обёртка Icon
    /// этим хендлом не владеет (см. комментарий у поля _iconHandle выше).
    /// </summary>
    private static (Icon Icon, IntPtr Handle) CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using var backgroundBrush = new SolidBrush(Color.FromArgb(255, 30, 144, 255));
            g.FillEllipse(backgroundBrush, 1, 1, 30, 30);

            using var whiteBrush = new SolidBrush(Color.White);
            using var whitePen = new Pen(Color.White, 2f);

            // Капсула микрофона.
            g.FillEllipse(whiteBrush, 12, 6, 8, 14);
            g.FillRectangle(whiteBrush, 12, 13, 8, 7);

            // Дужка (подставка) и стойка микрофона.
            g.DrawArc(whitePen, 8, 11, 16, 14, 0, 180);
            g.DrawLine(whitePen, 16, 25, 16, 28);
            g.DrawLine(whitePen, 11, 28, 21, 28);
        }

        IntPtr hIcon = bitmap.GetHicon();
        return (Icon.FromHandle(hIcon), hIcon);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        // Сама NotifyIcon.Dispose() выше не освобождает присвоенный Icon
        // (и тем более не знает про исходный HICON) — освобождаем его здесь.
        DestroyIcon(_iconHandle);
    }
}
