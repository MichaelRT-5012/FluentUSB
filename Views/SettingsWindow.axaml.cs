using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using FluentUSB.Models;
using FluentUSB.Services;
using FluentUSB.ViewModels;

namespace FluentUSB.Views;

/// <summary>外观与弹窗设置窗口（从托盘菜单或主窗口齿轮按钮打开）。</summary>
public partial class SettingsWindow : Window, IAppearanceHost
{
    // 导航项高度 38 + 间距 2
    private const double NavItemStride = 40;

    private bool _fitted;
    private SettingsViewModel? _viewModel;
    private readonly TranslateTransform _indicatorTransform = new();
    private readonly DispatcherTimer _indicatorTimer;
    private double _indicatorVelocity;
    private double _indicatorTarget;

    public SettingsWindow()
    {
        InitializeComponent();

        NavIndicator.RenderTransform = _indicatorTransform;
        _indicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _indicatorTimer.Tick += OnIndicatorTick;
        Closed += (_, _) => _indicatorTimer.Stop();

        Opened += (_, _) =>
        {
            // 小屏幕下窗口不能超出工作区，否则底部按钮会被裁掉
            Dispatcher.UIThread.Post(FitToScreen, DispatcherPriority.Loaded);
            BackdropService.Apply(this);
            AnimateSection(AppearanceSection, 10);
            MoveIndicatorToCurrent(immediate: true);
        };

        ActualThemeVariantChanged += (_, _) => BackdropService.Apply(this);
        DataContextChanged += OnDataContextChanged;
    }

    public Border? SurfaceHost => Surface;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as SettingsViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // 分类切换时让新内容淡入并轻微右移归位，避免生硬闪现
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.SectionIndex) || _viewModel is null)
            return;

        AnimateSection(_viewModel.IsAboutSection ? AboutSection : AppearanceSection);
        MoveIndicatorToCurrent(immediate: false);
    }

    /// <summary>把选中指示条滑到当前分类（弹簧阻尼，带一点回弹）。</summary>
    private void MoveIndicatorToCurrent(bool immediate)
    {
        _indicatorTarget = (_viewModel?.SectionIndex ?? 0) * NavItemStride;

        if (immediate)
        {
            _indicatorVelocity = 0;
            _indicatorTransform.Y = _indicatorTarget;
            return;
        }

        _indicatorTimer.Start();
    }

    private void OnIndicatorTick(object? sender, EventArgs e)
    {
        const double dt = 0.016;
        const double stiffness = 240;
        const double damping = 17;

        // 欠阻尼弹簧：先快速滑向目标，越过少许后回弹稳定
        var y = _indicatorTransform.Y;
        var acceleration = (_indicatorTarget - y) * stiffness - _indicatorVelocity * damping;
        _indicatorVelocity += acceleration * dt;
        var next = y + _indicatorVelocity * dt;
        _indicatorTransform.Y = next;

        if (Math.Abs(_indicatorTarget - next) < 0.4 && Math.Abs(_indicatorVelocity) < 2)
        {
            _indicatorTransform.Y = _indicatorTarget;
            _indicatorVelocity = 0;
            _indicatorTimer.Stop();
        }
    }

    private static void AnimateSection(Control section, double offset = 14)
    {
        section.Opacity = 0;
        section.RenderTransform = TransformOperations.Parse($"translateX({offset:0}px)");

        // 等一帧，让初始状态生效后再过渡到终态
        Dispatcher.UIThread.Post(() =>
        {
            section.Opacity = 1;
            section.RenderTransform = TransformOperations.Parse("translateX(0px)");
        }, DispatcherPriority.Background);
    }

    /// <summary>把窗口尺寸限制在当前屏幕工作区内并重新居中（只在首次打开时执行）。</summary>
    private void FitToScreen()
    {
        if (_fitted)
            return;

        _fitted = true;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            Log.Warn("设置窗口适配失败：未找到当前屏幕");
            return;
        }

        var area = screen.WorkingArea;
        var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;

        // 留出 24px 边距，避免窗口贴边或被任务栏遮挡
        var maxWidth = Math.Max(MinWidth, area.Width / scale - 24);
        var maxHeight = Math.Max(MinHeight, area.Height / scale - 24);

        var width = Math.Min(Width, maxWidth);
        var height = Math.Min(Height, maxHeight);
        if (width <= 0 || height <= 0)
            return;

        Width = width;
        Height = height;
        Position = new PixelPoint(
            area.X + (int)Math.Max(0, (area.Width - width * scale) / 2),
            area.Y + (int)Math.Max(0, (area.Height - height * scale) / 2));
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>用示例盘信息预览当前的插入弹窗外观（改完设置可直接看效果）。</summary>
    private void OnPreviewToastClick(object? sender, RoutedEventArgs e)
    {
        var toast = new UsbToastWindow(new UsbDriveInfo(
            "D:", @"D:\", "示例 U 盘", "exFAT",
            15_902_400_000, 3_412_000_000));
        toast.Show();
    }
}
