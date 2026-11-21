using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FluentUSB.Controls;
using FluentUSB.Models;
using FluentUSB.Services;
using FluentUSB.ViewModels;

namespace FluentUSB.Views;

/// <summary>外观与弹窗设置窗口（从托盘菜单或主窗口齿轮按钮打开）。</summary>
public partial class SettingsWindow : Window, IAppearanceHost
{
    private bool _fitted;
    private int _sectionIndex;
    private CancellationTokenSource? _sectionHideCts;
    private SettingsViewModel? _viewModel;
    private UsbToastWindow? _previewToast;
    private int _aboutCardClicks;
    private DateTime _lastAboutCardClick;
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
            Dispatcher.UIThread.Post(() => MoveIndicatorToCurrent(immediate: true),
                DispatcherPriority.Loaded);
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
        {
            _sectionIndex = _viewModel.SectionIndex;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    // 分类切换：旧内容向切换的反方向淡出，新内容从切换方向进入
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.SectionIndex) || _viewModel is null)
            return;

        var target = _viewModel.SectionIndex;
        TransitionSections(_sectionIndex, target);
        _sectionIndex = target;

        MoveIndicatorToCurrent(immediate: false);
    }

    /// <summary>按切换方向做交叉过渡，避免内容生硬替换。</summary>
    private void TransitionSections(int from, int to)
    {
        if (from == to)
            return;

        var outgoing = from == 1 ? AboutSection : AppearanceSection;
        var incoming = to == 1 ? AboutSection : AppearanceSection;

        ContentScroller.Offset = default;

        // 旧内容短促淡出，而不是瞬间消失
        EnsureFade(outgoing);
        outgoing.Opacity = 0;

        incoming.IsVisible = true;
        incoming.Opacity = 1;

        // 同步复位子项（复位本身不带过渡），否则会先完整显示一帧再开始入场动画
        IntroAnimation.Restart(incoming);

        // 淡出结束后隐藏旧内容；被快速切回时取消，避免误隐藏
        _sectionHideCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sectionHideCts = cts;
        _ = Task.Delay(160, cts.Token).ContinueWith(task =>
        {
            if (task.IsCanceled)
                return;

            Dispatcher.UIThread.Post(() => outgoing.IsVisible = false);
        }, TaskScheduler.Default);
    }

    /// <summary>分类面板的淡出过渡只挂一次。</summary>
    private static void EnsureFade(Control panel)
    {
        if (panel.Transitions is not null)
            return;

        panel.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(150),
            },
        };
    }

    /// <summary>把选中指示条滑到当前分类（弹簧阻尼，带一点回弹）。</summary>
    private void MoveIndicatorToCurrent(bool immediate)
    {
        var target = (_viewModel?.SectionIndex ?? 0) == 1 ? NavAbout : NavAppearance;

        // 按实际布局取目标项中心，导航项之间插入分组标题也不会错位
        var center = target.TranslatePoint(new Point(0, target.Bounds.Height / 2), NavLayer)?.Y ?? 0;
        _indicatorTarget = Math.Max(0, center - NavIndicator.Height / 2);

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
        // 欠阻尼弹簧：略快于临界阻尼，越过目标约 8% 后回弹稳定（约 450ms）
        const double stiffness = 380;
        const double damping = 24;

        // 分小步积分，高刷新率屏幕下轨迹更平滑
        const int steps = 4;
        const double dt = 0.016 / steps;

        var y = _indicatorTransform.Y;
        for (var i = 0; i < steps; i++)
        {
            var acceleration = (_indicatorTarget - y) * stiffness - _indicatorVelocity * damping;
            _indicatorVelocity += acceleration * dt;
            y += _indicatorVelocity * dt;
        }

        _indicatorTransform.Y = y;

        if (Math.Abs(_indicatorTarget - y) < 0.3 && Math.Abs(_indicatorVelocity) < 1.5)
        {
            _indicatorTransform.Y = _indicatorTarget;
            _indicatorVelocity = 0;
            _indicatorTimer.Stop();
        }
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

    /// <summary>关于页的应用卡片连点 5 次才显示构建时间（排障用，不打扰普通用户）。</summary>
    private void OnAboutAppCardClick(object? sender, RoutedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (now - _lastAboutCardClick > TimeSpan.FromSeconds(1.5))
            _aboutCardClicks = 0;
        _lastAboutCardClick = now;

        if (++_aboutCardClicks < 5)
            return;

        _aboutCardClicks = 0;
        BuildTimeRow.IsVisible = true;
    }

    private void OnOpenSettingsFolderClick(object? sender, RoutedEventArgs e) =>
        OpenFolder((DataContext as SettingsViewModel)?.SettingsFolderPath);

    private void OnOpenLogFolderClick(object? sender, RoutedEventArgs e) =>
        OpenFolder((DataContext as SettingsViewModel)?.LogFolderPath);

    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"打开目录失败：{path}", ex);
        }
    }

    /// <summary>用示例盘信息预览当前的插入弹窗外观（改完设置可直接看效果）。</summary>
    private void OnPreviewToastClick(object? sender, RoutedEventArgs e)
    {
        // 连续点击预览时先收起上一个，避免多个预览窗完全重叠
        _previewToast?.Dismiss();

        var toast = new UsbToastWindow(new UsbDriveInfo(
            "D:", @"D:\", "示例 U 盘", "exFAT",
            15_902_400_000, 3_412_000_000), preview: true);
        toast.Closed += (_, _) =>
        {
            if (ReferenceEquals(_previewToast, toast))
                _previewToast = null;
        };

        _previewToast = toast;
        toast.Show();
    }
}
