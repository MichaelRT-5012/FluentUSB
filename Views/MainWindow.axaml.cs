using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FluentUSB.Services;
using FluentUSB.ViewModels;

namespace FluentUSB.Views;

public partial class MainWindow : Window, IAppearanceHost
{
    private int _demoClickCount;
    private DateTime _lastDemoClick;

    /// <summary>为 true 时允许窗口真正关闭（仅托盘「退出」时设置）。</summary>
    public bool AllowRealClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        // 用隧道事件计数：按钮会吃掉冒泡事件，但隧道阶段仍能收到每一次点击
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);

        // 窗口显示后才有句柄，此时才能应用原生背景效果
        Opened += (_, _) => BackdropService.Apply(this);
        ActualThemeVariantChanged += (_, _) => BackdropService.Apply(this);
    }

    public Border? SurfaceHost => Surface;

    private void OnSettingsClick(object? sender, RoutedEventArgs e) =>
        (Application.Current as App)?.OpenSettings();

    /// <summary>连点主窗口任意位置 5 次后，在设置旁显示示例 U 盘入口（调试用）。</summary>
    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var now = DateTime.UtcNow;
        if (now - _lastDemoClick > TimeSpan.FromSeconds(1.5))
            _demoClickCount = 0;

        _lastDemoClick = now;

        if (++_demoClickCount < 5)
            return;

        _demoClickCount = 0;
        (DataContext as MainWindowViewModel)?.RevealDemo();
    }

    // 常驻程序习惯：点关闭按钮只隐藏窗口并保留托盘图标，不退出进程
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowRealClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
