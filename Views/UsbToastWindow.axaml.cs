using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using FluentUSB.Models;
using FluentUSB.Services;
using FluentUSB.ViewModels;

namespace FluentUSB.Views;

/// <summary>
/// U 盘插入提示弹窗：展示盘名/盘符/容量，点击一键打开。
/// 倒计时结束自动关闭（鼠标悬停暂停倒计时）；
/// 弹出位置与停留时长由设置页控制（默认屏幕左下角、8 秒）。
/// </summary>
public partial class UsbToastWindow : Window, IAppearanceHost
{
    private readonly DispatcherTimer _countdownTimer;
    private readonly TimeSpan _duration;
    private TimeSpan _remaining;
    private bool _paused;
    private bool _dismissed;

    /// <summary>仅供设计器预览使用；运行时请使用带 drive 参数的构造函数。</summary>
    public UsbToastWindow()
        : this(new UsbDriveInfo("D:", @"D:\", "示例 U 盘", "exFAT", 15_902_400_000, 3_412_000_000))
    {
    }

    public UsbToastWindow(UsbDriveInfo drive)
    {
        InitializeComponent();

        DataContext = new UsbDriveViewModel(drive);
        DriveLetter = drive.DriveLetter;

        var settings = SettingsStore.Current;
        _duration = TimeSpan.FromSeconds(settings.PopupDurationSeconds);
        _remaining = _duration;
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _countdownTimer.Tick += OnCountdownTick;

        Opened += OnWindowOpened;
        Closed += (_, _) =>
        {
            _countdownTimer.Stop();
            SettingsStore.ToastChanged -= OnToastAppearanceChanged;
        };
        SettingsStore.ToastChanged += OnToastAppearanceChanged;
        ActualThemeVariantChanged += (_, _) => ApplyToastAppearance();
        PointerPressed += OnToastPointerPressed;
        PointerEntered += (_, _) => _paused = true;
        PointerExited += (_, _) => _paused = false;
    }

    public Border? SurfaceHost => RootCard;

    public bool UsesToastAppearance => true;

    // 弹窗打开期间改「插入弹窗外观」，让当前弹窗即时刷新（设置页的「预览」也靠它）
    private void OnToastAppearanceChanged() => ApplyToastAppearance();

    /// <summary>
    /// 按「插入弹窗外观」设置调整卡片形态并重新应用背景：
    /// 卡片式 = 圆角卡片 + 自绘阴影；整窗式 = 窗口与卡片等大，交给系统材质与圆角。
    /// </summary>
    public void ApplyToastAppearance()
    {
        var windowStyle = SettingsStore.Current.ToastStyle == ToastStyle.Window;

        if (windowStyle)
        {
            // 整窗式没有余量画自绘阴影，边框也交给系统材质
            RootCard.Margin = default;
            RootCard.CornerRadius = default;
            RootCard.BorderThickness = default;
            RootCard.BoxShadow = default;
        }
        else
        {
            RootCard.Margin = new Thickness(8);
            RootCard.CornerRadius = new CornerRadius(8);
            RootCard.BorderThickness = new Thickness(1);
            RootCard.BoxShadow = BoxShadows.Parse("0 8 24 -8 #66000000");
        }

        RootCard.Classes.Set("Static", windowStyle);

        if (TryGetPlatformHandle() is { } handle && handle.Handle != IntPtr.Zero)
            BackdropNative.SetRoundedCorners(handle.Handle, windowStyle);

        BackdropService.Apply(this);
    }

    /// <summary>该弹窗对应的盘符（如 "D:"），用于去重与拔出时回收。</summary>
    public string DriveLetter { get; }

    /// <summary>同角落已有弹窗时的堆叠序号（0 表示贴边），由 App 在显示或重排时设置。</summary>
    public int SlotIndex { get; private set; }

    /// <summary>更新堆叠序号；窗口已显示时立即重新定位。</summary>
    public void SetSlot(int slot)
    {
        SlotIndex = slot;
        if (IsVisible)
            PositionOnScreen();
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        // 卡片四周留了 8px 阴影边距；卡片式只画底色，整窗式才套用系统材质
        ApplyToastAppearance();
        PositionOnScreen();
        // 延后一帧再切换到 Show 状态，触发入场动画（淡入 + 上浮）
        Dispatcher.UIThread.Post(() => RootCard.Classes.Add("Show"), DispatcherPriority.Render);
        _countdownTimer.Start();
    }

    /// <summary>
    /// 按设置里的停靠角落把弹窗放到某个屏幕工作区（默认左下角）。
    /// 优先使用鼠标所在屏幕，避免老师用扩展屏时弹窗出现在看不见的主屏。
    /// </summary>
    private void PositionOnScreen()
    {
        var screen = FindTargetScreen();
        if (screen is null) return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)(Width * scale);
        var height = (int)(Height * scale);

        var settings = SettingsStore.Current;
        var margin = settings.PopupMargin;

        var left = settings.PopupPosition is PopupCorner.BottomLeft or PopupCorner.TopLeft;
        var bottom = settings.PopupPosition is PopupCorner.BottomLeft or PopupCorner.BottomRight;

        // 多个 U 盘同时插入时向上（或向下）堆叠，避免重叠
        var slotOffset = SlotIndex * (int)((Height + 8) * scale);

        Position = new PixelPoint(
            left ? area.X + margin : area.Right - width - margin,
            bottom ? area.Bottom - height - margin - slotOffset : area.Y + margin + slotOffset);
    }

    /// <summary>鼠标所在的屏幕；取不到时回退到主屏。</summary>
    private Screen? FindTargetScreen()
    {
        if (TryGetCursorPosition(out var point))
        {
            try
            {
                var screen = Screens.ScreenFromPoint(point);
                if (screen is not null)
                    return screen;
            }
            catch (Exception)
            {
                // 坐标不在任何屏幕内时回退主屏
            }
        }

        return Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        if (_paused) return;

        _remaining -= _countdownTimer.Interval;
        Countdown.Value = Math.Max(0, _remaining.TotalMilliseconds / _duration.TotalMilliseconds * 100);

        if (_remaining <= TimeSpan.Zero)
            Dismiss();
    }

    // 点击弹窗任意位置 → 打开 U 盘（M1 临时用资源管理器，M2 替换为内建文件管理器）
    private void OnToastPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is UsbDriveViewModel vm)
            vm.OpenDriveCommand.Execute(null);
        Dismiss();
    }

    // 关闭按钮自行消化按压事件，避免冒泡触发"点击打开 U 盘"
    private void OnCloseButtonPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Dismiss();

    /// <summary>播放退场动画后关闭窗口（可重复调用）。</summary>
    public void Dismiss()
    {
        if (_dismissed) return;
        _dismissed = true;
        _countdownTimer.Stop();
        RootCard.Classes.Remove("Show");
        DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(240));
    }

    private static bool TryGetCursorPosition(out PixelPoint point)
    {
        if (GetCursorPos(out var native))
        {
            point = new PixelPoint(native.X, native.Y);
            return true;
        }

        point = default;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
}
