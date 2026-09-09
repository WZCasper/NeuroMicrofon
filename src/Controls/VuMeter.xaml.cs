using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NeuroMicrophone.Controls;

/// <summary>
/// Живой VU-метр: заполненная полоса отображает сглаженный RMS-уровень,
/// тонкая вертикальная риска — пиковый уровень в классическом режиме
/// "peak-hold": риска мгновенно перескакивает на новый пик, удерживается
/// на месте некоторое время (HoldMs), а затем плавно спадает со скоростью
/// DecayDbPerSecond — так его успевает заметить глаз, в отличие от
/// мгновенного значения, дёргающегося кадр в кадр.
/// Диапазон шкалы: от MinDb (тихо) до MaxDb (0 dBFS, максимум).
/// </summary>
public partial class VuMeter : UserControl
{
    public const double MinDb = -60.0;
    public const double MaxDb = 0.0;

    private const double HoldMs = 1500.0;
    private const double DecayDbPerSecond = 20.0;
    private static readonly TimeSpan DecayTickInterval = TimeSpan.FromMilliseconds(33);

    public static readonly DependencyProperty RmsDbProperty =
        DependencyProperty.Register(nameof(RmsDb), typeof(double), typeof(VuMeter),
            new PropertyMetadata(MinDb, OnRmsChanged));

    public static readonly DependencyProperty PeakDbProperty =
        DependencyProperty.Register(nameof(PeakDb), typeof(double), typeof(VuMeter),
            new PropertyMetadata(MinDb, OnPeakChanged));

    public double RmsDb
    {
        get => (double)GetValue(RmsDbProperty);
        set => SetValue(RmsDbProperty, value);
    }

    public double PeakDb
    {
        get => (double)GetValue(PeakDbProperty);
        set => SetValue(PeakDbProperty, value);
    }

    private readonly DispatcherTimer _decayTimer;
    private double _heldPeakDb = MinDb;
    private double _holdRemainingMs;

    public VuMeter()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateVisual();

        _decayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = DecayTickInterval };
        _decayTimer.Tick += OnDecayTick;

        Loaded += (_, _) => _decayTimer.Start();
        Unloaded += (_, _) => _decayTimer.Stop();
    }

    private static void OnRmsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((VuMeter)d).UpdateVisual();
    }

    private static void OnPeakChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var meter = (VuMeter)d;
        double newPeak = (double)e.NewValue;

        // Новый пик выше удерживаемого — сразу перескакиваем на него и
        // заново запускаем время удержания. Если новый пик ниже — риска
        // продолжает спадать по расписанию таймера, а не прыгает вниз.
        if (newPeak >= meter._heldPeakDb)
        {
            meter._heldPeakDb = newPeak;
            meter._holdRemainingMs = HoldMs;
        }

        meter.UpdateVisual();
    }

    private void OnDecayTick(object? sender, EventArgs e)
    {
        if (_holdRemainingMs > 0)
        {
            _holdRemainingMs -= DecayTickInterval.TotalMilliseconds;
        }
        else if (_heldPeakDb > MinDb)
        {
            double decayStep = DecayDbPerSecond * (DecayTickInterval.TotalMilliseconds / 1000.0);
            _heldPeakDb = Math.Max(MinDb, _heldPeakDb - decayStep);
            UpdateVisual();
        }
    }

    private void UpdateVisual()
    {
        double width = TrackGrid.ActualWidth;
        if (width <= 0) return;

        double rmsFraction = Normalize(RmsDb);
        double peakFraction = Normalize(_heldPeakDb);

        RmsBar.Width = width * rmsFraction;
        PeakMarker.Margin = new Thickness(Math.Max(0, width * peakFraction - 1), 0, 0, 0);

        RmsBar.Fill = rmsFraction switch
        {
            > 0.9 => (Brush)FindResource("DangerBrush"),
            > 0.7 => (Brush)FindResource("SuccessBrush"),
            _ => (Brush)FindResource("AccentBrush"),
        };
    }

    private static double Normalize(double db)
    {
        double clamped = Math.Clamp(db, MinDb, MaxDb);
        return (clamped - MinDb) / (MaxDb - MinDb);
    }
}
