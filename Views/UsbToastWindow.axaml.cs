using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FluentUSB.Models;
using FluentUSB.ViewModels;

namespace FluentUSB.Views;

/// <summary>
/// U 盘插入提示弹窗：展示盘名/盘符/容量，点击一键打开。
/// 倒计时结束自动关闭（鼠标悬停暂停倒计时）；
/// 弹出位置由 UiOptions.PopupPosition 控制（默认屏幕左下角）。
/// </summary>
public partial class UsbToastWindow : Window
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

        // 透明度级别集中由 UiOptions 控制（与主窗口一致）
        TransparencyLevelHint = UiOptions.TransparencyHint;
        DataContext = new UsbDriveViewModel(drive);
        DriveLetter = drive.DriveLetter;

        _duration = UiOptions.PopupDuration;
        _remaining = _duration;
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _countdownTimer.Tick += OnCountdownTick;

        Opened += OnWindowOpened;
        PointerPressed += OnToastPointerPressed;
        PointerEntered += (_, _) => _paused = true;
        PointerExited += (_, _) => _paused = false;
    }

    /// <summary>该弹窗对应的盘符（如 "D:"），用于去重与拔出时回收。</summary>
    public string DriveLetter { get; }

    /// <summary>同角落已有弹窗时的堆叠序号（0 表示贴边），由 App 在显示前设置。</summary>
    public int SlotIndex { get; set; }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        PositionOnScreen();
        // 延后一帧再切换到 Show 状态，触发入场动画（淡入 + 上浮）
        Dispatcher.UIThread.Post(() => RootCard.Classes.Add("Show"), DispatcherPriority.Render);
        _countdownTimer.Start();
    }

    /// <summary>按 UiOptions.PopupPosition 把弹窗放到主屏工作区的角落（默认左下角）。</summary>
    private void PositionOnScreen()
    {
        var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)(Width * scale);
        var height = (int)(Height * scale);
        var margin = UiOptions.PopupMargin;

        var left = UiOptions.PopupPosition is PopupCorner.BottomLeft or PopupCorner.TopLeft;
        var bottom = UiOptions.PopupPosition is PopupCorner.BottomLeft or PopupCorner.BottomRight;

        // 多个 U 盘同时插入时向上（或向下）堆叠，避免重叠
        var slotOffset = SlotIndex * (int)((Height + 8) * scale);

        Position = new PixelPoint(
            left ? area.X + margin : area.Right - width - margin,
            bottom ? area.Bottom - height - margin - slotOffset : area.Y + margin + slotOffset);
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
}
