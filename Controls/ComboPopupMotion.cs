using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentUSB.Services;

namespace FluentUSB.Controls;

/// <summary>
/// 给 ComboBox 的下拉浮层加展开动画。
/// 浮层不在 ComboBox 的视觉树里（内容挂在独立的 PopupRoot 上），
/// 所以在展开时按 TemplatedParent 反查 Popup，再用逐帧动画放大淡入。
/// </summary>
public static class ComboPopupMotion
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(200);

    private const double HiddenScale = 0.9;

    // 缩放原点固定在顶部中心，避免每次展开都重新构造
    private static readonly RelativePoint TopCenterOrigin = new(0.5, 0, RelativeUnit.Relative);

    // 关闭状态的缩放变换只解析一次，避免每次关闭都走字符串解析
    private static readonly TransformOperations HiddenTransform =
        TransformOperations.Parse($"scale({HiddenScale})");

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<ComboBox, bool>("Enabled", typeof(ComboPopupMotion));

    static ComboPopupMotion()
    {
        EnabledProperty.Changed.AddClassHandler<ComboBox>(HandleEnabledChanged);
    }

    public static void SetEnabled(ComboBox combo, bool value) => combo.SetValue(EnabledProperty, value);

    public static bool GetEnabled(ComboBox combo) => combo.GetValue(EnabledProperty);

    private static void HandleEnabledChanged(ComboBox combo, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is not true)
            return;

        combo.AttachedToVisualTree += (_, _) => SafeEnsure(combo);
        combo.TemplateApplied += (_, _) => SafeEnsure(combo);
        combo.PropertyChanged -= OnComboPropertyChanged;
        combo.PropertyChanged += OnComboPropertyChanged;

        SafeEnsure(combo);
    }

    private static void OnComboPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not ComboBox combo || e.Property != ComboBox.IsDropDownOpenProperty)
            return;

        // 展开的那一刻浮层才真正存在，此时再找一次并直接开始动画
        if (e.NewValue is true)
            Dispatcher.UIThread.Post(() => SafeEnsure(combo), DispatcherPriority.Loaded);
    }

    private static void SafeEnsure(ComboBox combo)
    {
        try
        {
            EnsureHooked(combo);
        }
        catch (Exception exception)
        {
            Log.Warn($"[combo] 浮层挂接失败：{exception.Message}");
        }
    }

    private static void EnsureHooked(ComboBox combo)
    {
        var popup = FindPopup(combo);
        if (popup is null)
            return;

        popup.Opened -= OnPopupOpened;
        popup.Closed -= OnPopupClosed;
        popup.Opened += OnPopupOpened;
        popup.Closed += OnPopupClosed;

        // 已经展开时不能再置为隐藏，直接播展开动画
        if (popup.IsOpen)
            Reveal(popup);
        else
            Prepare(popup);
    }

    /// <summary>浮层挂在独立的 PopupRoot 上，需要从顶层按 TemplatedParent 反查。</summary>
    private static Popup? FindPopup(ComboBox combo)
    {
        var local = combo.GetLogicalDescendants().OfType<Popup>().FirstOrDefault()
            ?? combo.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
        if (local is not null)
            return local;

        var top = TopLevel.GetTopLevel(combo);
        if (top is null)
            return null;

        bool Owned(Popup popup) => ReferenceEquals(popup.TemplatedParent, combo);

        return top.GetLogicalDescendants().OfType<Popup>().FirstOrDefault(Owned)
            ?? top.GetVisualDescendants().OfType<Popup>().FirstOrDefault(Owned);
    }

    private static void OnPopupOpened(object? sender, EventArgs e)
    {
        if (sender is Popup popup)
            Reveal(popup);
    }

    private static void OnPopupClosed(object? sender, EventArgs e)
    {
        if (sender is Popup popup)
            Prepare(popup);
    }

    /// <summary>放大淡入：从 90% 尺寸、透明过渡到原尺寸、完全可见。</summary>
    private static void Reveal(Popup popup)
    {
        try
        {
            if (popup.Child is not Control content)
                return;

            var scale = new ScaleTransform(HiddenScale, HiddenScale);
            content.RenderTransformOrigin = TopCenterOrigin;
            content.RenderTransform = scale;
            content.Opacity = 0;

            var clock = Stopwatch.StartNew();
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };

            timer.Tick += (_, _) =>
            {
                var progress = Math.Min(1, clock.Elapsed.TotalMilliseconds / Duration.TotalMilliseconds);

                // 减速曲线：开头快、结尾慢
                var eased = 1 - Math.Pow(1 - progress, 3);
                content.Opacity = eased;

                var factor = HiddenScale + (1 - HiddenScale) * eased;
                scale.ScaleX = factor;
                scale.ScaleY = factor;

                if (progress < 1 && popup.IsOpen)
                    return;

                content.Opacity = 1;
                scale.ScaleX = 1;
                scale.ScaleY = 1;
                timer.Stop();
            };

            timer.Start();
        }
        catch (Exception exception)
        {
            Log.Warn($"[combo] 展开动画失败：{exception.Message}");
        }
    }

    /// <summary>把浮层内容恢复为「待展开」状态：不可见且略小，缩放原点在顶部中心。</summary>
    private static void Prepare(Popup popup)
    {
        if (popup.Child is not Control content)
            return;

        content.RenderTransformOrigin = TopCenterOrigin;
        content.Opacity = 0;
        content.RenderTransform = HiddenTransform;
    }
}
