using System;
using System.Drawing;
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
    private readonly NotifyIcon _notifyIcon;
    private readonly Window _window;

    /// <summary>Вызывается при выборе пункта "Выход" в контекстном меню трея.</summary>
    public event Action? ExitRequested;

    public TrayIconService(Window window)
    {
        _window = window;

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Показать NeuroMicrophone", null, (_, _) => ShowWindow());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Visible = false,
            Text = "NeuroMicrophone",
            ContextMenuStrip = contextMenu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();
    }

    public void ShowInTray()
    {
        _notifyIcon.Visible = true;
        _window.Hide();
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
    /// использования внешних файлов ресурсов.
    /// </summary>
    private static Icon CreateTrayIcon()
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
        return Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
