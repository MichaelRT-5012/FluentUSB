using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace FluentUSB.Controls;

/// <summary>
/// 入场动画，参数与 ClassIsland 的 StackPanelIntroAnimationBehavior 一致：
/// 子元素逐项错峰 25ms，从 Y=50 上滑到 0 并淡入，时长 0.75s，
/// 缓动为 cubic-bezier(0.00, 1.00, 0.00, 1.00)。
/// 只使用 Avalonia 原生 Transitions，失败时静默降级为直接显示，避免 UI 线程崩溃。
/// </summary>
public static class IntroAnimation
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Panel, bool>("Enabled", typeof(IntroAnimation), false);

    private static readonly ConditionalWeakTable<Panel, DispatcherTimer> Timers = new();

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(25);
    private const double OffsetY = 50;

    // 两个变换只解析一次，避免每次入场都走字符串解析
    private static readonly TransformOperations HiddenTransform =
        TransformOperations.Parse($"translateY({OffsetY}px)");
    private static readonly TransformOperations ShownTransform =
        TransformOperations.Parse("translateY(0px)");

    public static void SetEnabled(Panel panel, bool value) => panel.SetValue(EnabledProperty, value);
    public static bool GetEnabled(Panel panel) => panel.GetValue(EnabledProperty);

    static IntroAnimation()
    {
        EnabledProperty.Changed.AddClassHandler<Panel>(OnEnabledChanged);
    }

    private static void OnEnabledChanged(Panel panel, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
            return;

        if (panel.IsLoaded)
        {
            Dispatcher.UIThread.Post(() => Play(panel), DispatcherPriority.Background);
            return;
        }

        panel.Loaded += OnLoaded;
    }

    private static void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Panel panel)
            return;

        panel.Loaded -= OnLoaded;
        Play(panel);
    }

    /// <summary>重新播放入场动画（切换设置分类时调用）。</summary>
    public static void Restart(Panel panel)
    {
        if (!panel.IsLoaded)
            return;

        Play(panel);
    }

    private static void Play(Panel panel)
    {
        try
        {
            if (Timers.TryGetValue(panel, out var running))
            {
                running.Stop();
                Timers.Remove(panel);
            }

            var targets = panel.Children
                .OfType<Control>()
                .Where(x => x.IsVisible)
                .ToList();

            if (targets.Count == 0)
                return;

            foreach (var c in targets)
            {
                // 先摘掉上一轮的过渡再复位，避免从终态缓慢"倒放"回起点造成闪烁
                c.Transitions = null;
                c.Opacity = 0;
                c.RenderTransform = HiddenTransform;
                EnsureTransitions(c);
            }

            var index = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = Stagger };
            timer.Tick += (_, _) =>
            {
                if (index >= targets.Count)
                {
                    timer.Stop();
                    Timers.Remove(panel);
                    return;
                }

                var c = targets[index++];
                c.Opacity = 1;
                c.RenderTransform = ShownTransform;
            };
            Timers.AddOrUpdate(panel, timer);
            timer.Start();
        }
        catch
        {
            ShowAll(panel);
        }
    }

    private static void EnsureTransitions(Control c)
    {
        var easing = new SplineEasing(0, 1, 0, 1);
        c.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = Duration,
                Easing = easing
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = Duration,
                Easing = easing
            },
        };
    }

    private static void ShowAll(Panel panel)
    {
        foreach (var c in panel.Children.OfType<Control>())
        {
            c.Opacity = 1;
            c.RenderTransform = ShownTransform;
        }
    }
}
