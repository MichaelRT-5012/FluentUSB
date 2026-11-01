using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FluentUSB.Models;
using FluentUSB.Services;
using FluentUSB.ViewModels;
using FluentUSB.Views;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

// 本应用依赖资源管理器与 Win32 设备消息，仅面向 Windows
[assembly: SupportedOSPlatform("windows")]

namespace FluentUSB;

public partial class App : Application
{
    private readonly Dictionary<string, UsbToastWindow> _usbToasts = new();
    private readonly MainWindowViewModel _mainViewModel = new();
    private MainWindow? _mainWindow;
    private UsbDeviceMonitor? _usbMonitor;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 主题集中由 UiOptions 控制（预留深浅切换）
        RequestedThemeVariant = UiOptions.Theme;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 仅允许托盘「退出」结束进程；关闭主窗口只隐藏，不退出
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 启动时不显示主窗口，只保留托盘图标；主窗口在首次需要时创建
        }

        StartUsbMonitor();

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>启动 U 盘插拔监听（M1：插入弹窗提示，点击一键打开）。</summary>
    private void StartUsbMonitor()
    {
        _usbMonitor = new UsbDeviceMonitor();
        _usbMonitor.DriveArrived += OnUsbDriveArrived;
        _usbMonitor.DriveRemoved += OnUsbDriveRemoved;
        _usbMonitor.Start();
    }

    // U 盘插入：弹出提示窗（同一盘符已有弹窗时先收起旧的），并同步到主窗口列表
    private void OnUsbDriveArrived(UsbDriveInfo drive)
    {
        _mainViewModel.AddDrive(drive);

        if (_usbToasts.Remove(drive.DriveLetter, out var existing))
            existing.Close();

        var toast = new UsbToastWindow(drive) { SlotIndex = _usbToasts.Count };
        toast.Closed += (_, _) => _usbToasts.Remove(drive.DriveLetter);
        _usbToasts[drive.DriveLetter] = toast;
        toast.Show();
    }

    // U 盘拔出：收起对应提示窗，并从主窗口列表移除
    private void OnUsbDriveRemoved(string driveLetter)
    {
        _mainViewModel.RemoveDrive(driveLetter);

        if (_usbToasts.Remove(driveLetter, out var toast))
            toast.Dismiss();
    }

    // 托盘左键：恢复主窗口
    private void OnTrayIconClicked(object? sender, EventArgs e) => ShowMainWindow();

    // 托盘菜单：显示主窗口
    private void OnShowMainWindowClick(object? sender, EventArgs e) => ShowMainWindow();

    // 托盘菜单：退出程序
    private void OnExitClick(object? sender, EventArgs e)
    {
        _usbMonitor?.Dispose();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (_mainWindow is not null)
                _mainWindow.AllowRealClose = true;
            desktop.Shutdown();
        }
    }

    private void ShowMainWindow()
    {
        _mainWindow ??= new MainWindow
        {
            DataContext = _mainViewModel,
        };

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }
}
