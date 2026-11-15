using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace FluentUSB.Controls;

/// <summary>
/// 带数值气泡的滑动条：拖动时在滑块上方显示一个小圆角数值框，跟随滑块移动。
/// </summary>
public partial class ValueSlider : UserControl
{
    // Fluent 主题下滑块直径
    private const double ThumbWidth = 20;

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<ValueSlider, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<ValueSlider, double>(nameof(Maximum), 100d);

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ValueSlider, double>(
            nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> TickFrequencyProperty =
        AvaloniaProperty.Register<ValueSlider, double>(nameof(TickFrequency), 1d);

    public static readonly StyledProperty<bool> IsSnapToTickEnabledProperty =
        AvaloniaProperty.Register<ValueSlider, bool>(nameof(IsSnapToTickEnabled), true);

    /// <summary>数值格式，例如 "{0:0}%"。</summary>
    public static readonly StyledProperty<string> FormatProperty =
        AvaloniaProperty.Register<ValueSlider, string>(nameof(Format), "{0:0}");

    static ValueSlider()
    {
        ValueProperty.Changed.AddClassHandler<ValueSlider>((slider, _) => slider.UpdateBubble());
        MinimumProperty.Changed.AddClassHandler<ValueSlider>((slider, _) => slider.UpdateBubble());
        MaximumProperty.Changed.AddClassHandler<ValueSlider>((slider, _) => slider.UpdateBubble());
    }

    public ValueSlider()
    {
        InitializeComponent();

        Inner.SizeChanged += (_, _) => UpdateBubblePosition();

        Inner.PointerPressed += (_, _) => ShowBubble();
        Inner.PointerMoved += (_, e) =>
        {
            if (e.GetCurrentPoint(Inner).Properties.IsLeftButtonPressed)
                ShowBubble();
        };
        Inner.PointerReleased += (_, _) => HideBubble();
        Inner.PointerCaptureLost += (_, _) => HideBubble();
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double TickFrequency
    {
        get => GetValue(TickFrequencyProperty);
        set => SetValue(TickFrequencyProperty, value);
    }

    public bool IsSnapToTickEnabled
    {
        get => GetValue(IsSnapToTickEnabledProperty);
        set => SetValue(IsSnapToTickEnabledProperty, value);
    }

    public string Format
    {
        get => GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    private void ShowBubble()
    {
        Bubble.Opacity = 1;
        UpdateBubble();
    }

    private void HideBubble() => Bubble.Opacity = 0;

    private void UpdateBubble()
    {
        BubbleText.Text = string.Format(CultureInfo.CurrentCulture, Format, Value);
        UpdateBubblePosition();
    }

    private void UpdateBubblePosition()
    {
        var width = Inner.Bounds.Width;
        if (width <= 0)
            return;

        var range = Maximum - Minimum;
        var ratio = range <= 0 ? 0 : Math.Clamp((Value - Minimum) / range, 0, 1);
        var center = ratio * (width - ThumbWidth) + ThumbWidth / 2;

        Bubble.Measure(Size.Infinity);
        var bubbleWidth = Bubble.DesiredSize.Width;
        var bubbleHeight = Bubble.DesiredSize.Height;

        Canvas.SetLeft(Bubble, Math.Clamp(center - bubbleWidth / 2, 0, Math.Max(0, width - bubbleWidth)));
        Canvas.SetTop(Bubble, -bubbleHeight - 4);
    }
}
