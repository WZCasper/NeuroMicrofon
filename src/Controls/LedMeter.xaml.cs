using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NeuroMicrophone.Controls;

/// <summary>
/// Сегментный (LED-style) индикатор уровня — по референсу дизайна: ряд
/// прямоугольных "светодиодов", часть которых горит в зависимости от
/// текущего уровня, с цветовыми зонами (зелёный → жёлтый → красный для
/// обычного уровня, жёлтый → красный для снижения усиления).
///
/// Сегменты создаются один раз программно (не через ItemsControl с
/// биндингами) и переиспользуются при каждом обновлении — то же решение
/// по производительности, что и в VuMeter: обновление на каждый тик
/// таймера метров (~30 раз/с) не должно создавать новые визуальные
/// элементы или пересчитывать привязки.
/// </summary>
public partial class LedMeter : UserControl
{
    private const double MinDb = -60.0;
    private const double MaxDb = 0.0;
    private const double GainReductionRangeDb = 24.0;

    public static readonly DependencyProperty LevelDbProperty =
        DependencyProperty.Register(nameof(LevelDb), typeof(double), typeof(LedMeter),
            new PropertyMetadata(MinDb, OnLevelChanged));

    public static readonly DependencyProperty IsGainReductionProperty =
        DependencyProperty.Register(nameof(IsGainReduction), typeof(bool), typeof(LedMeter),
            new PropertyMetadata(false, OnLevelChanged));

    public static readonly DependencyProperty SegmentCountProperty =
        DependencyProperty.Register(nameof(SegmentCount), typeof(int), typeof(LedMeter),
            new PropertyMetadata(24, OnSegmentCountChanged));

    private Border[] _segments = Array.Empty<Border>();

    /// <summary>
    /// Текущий уровень в дБ. Для обычного метра — RMS/пик сигнала (шкала
    /// MinDb..MaxDb). Для индикатора снижения усиления (IsGainReduction=true)
    /// — значение &lt;= 0, где 0 означает "нет сжатия" (шкала 0..-GainReductionRangeDb).
    /// </summary>
    public double LevelDb
    {
        get => (double)GetValue(LevelDbProperty);
        set => SetValue(LevelDbProperty, value);
    }

    public bool IsGainReduction
    {
        get => (bool)GetValue(IsGainReductionProperty);
        set => SetValue(IsGainReductionProperty, value);
    }

    public int SegmentCount
    {
        get => (int)GetValue(SegmentCountProperty);
        set => SetValue(SegmentCountProperty, value);
    }

    public LedMeter()
    {
        InitializeComponent();
        Loaded += (_, _) => BuildSegments();
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((LedMeter)d).UpdateSegments();
    }

    private static void OnSegmentCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((LedMeter)d).BuildSegments();
    }

    private void BuildSegments()
    {
        int count = Math.Max(1, SegmentCount);
        SegmentsHost.Children.Clear();
        SegmentsHost.Columns = count;

        _segments = new Border[count];
        for (int i = 0; i < count; i++)
        {
            var segment = new Border
            {
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(1, 0, 1, 0),
                Height = 14,
            };
            _segments[i] = segment;
            SegmentsHost.Children.Add(segment);
        }

        UpdateSegments();
    }

    private void UpdateSegments()
    {
        if (_segments.Length == 0) return;

        double fraction = IsGainReduction
            ? Normalize(-LevelDb, 0.0, GainReductionRangeDb)
            : Normalize(LevelDb, MinDb, MaxDb);

        int activeCount = (int)Math.Round(fraction * _segments.Length);
        int total = _segments.Length;

        for (int i = 0; i < total; i++)
        {
            _segments[i].Background = i < activeCount ? ColorForSegment(i, total) : GetOffBrush();
        }
    }

    private Brush ColorForSegment(int index, int total)
    {
        if (IsGainReduction)
        {
            return index > total * 0.7
                ? (Brush)FindResource("DangerBrush")
                : (Brush)FindResource("WarningBrush");
        }

        if (index < total * 0.62) return (Brush)FindResource("SuccessBrush");
        if (index < total * 0.83) return (Brush)FindResource("WarningBrush");
        return (Brush)FindResource("DangerBrush");
    }

    private Brush GetOffBrush() => (Brush)FindResource("MeterOffBrush");

    private static double Normalize(double value, double min, double max)
    {
        double clamped = Math.Clamp(value, min, max);
        return (clamped - min) / (max - min);
    }
}
