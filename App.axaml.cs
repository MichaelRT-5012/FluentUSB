using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentUSB.Models;
using FluentUSB.Services;
using FluentUSB.ViewModels;
using FluentUSB.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;

// 本应用依赖资源管理器与 Win32 设备消息，仅面向 Windows
[assembly: SupportedOSPlatform("windows")]

namespace FluentUSB;

public partial class App : Application
{
    // 当前存活的插入提示窗，顺序即叠放顺序（均为 UI 线程访问）
    private readonly List<UsbToastWindow> _usbToasts = [];
    private MainWindowViewModel _mainViewModel = new();
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private InstanceConflictWindow? _conflictWindow;
    private UsbDeviceMonitor? _usbMonitor;

    // 每个盘符一个内建文件浏览器，重复打开时只激活已有窗口
    private readonly Dictionary<string, FileExplorerWindow> _explorerWindows = new(StringComparer.OrdinalIgnoreCase);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 设置必须在创建任何窗口前加载（背景、主题、弹窗行为都依赖它）
        SettingsStore.Load();
        BackdropService.EnsureSupportedSettings();
        ApplyTheme();

        RegisterExceptionHandlers();

        // 第二个实例只会发来一个信号，由本实例弹出「已在运行」的提示窗口
        InstanceBridge.StartListening(OnSecondInstanceDetected);

        SettingsStore.AppearanceChanged += OnAppearanceChanged;
        SettingsStore.SurfaceChanged += OnSurfaceChanged;
        SettingsStore.BehaviorChanged += OnBehaviorChanged;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 仅允许托盘「退出」结束进程；关闭主窗口只隐藏，不退出
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 启动时不显示主窗口，只保留托盘图标；主窗口在首次需要时创建
            HandleStartupArguments(desktop);
        }

        // 主窗口显示时才会刷新列表，这里不再重复枚举一次驱动器
        StartUsbMonitor();

        Log.Info($"FluentUSB 已启动，背景={SettingsStore.Current.Backdrop}，系统={BackdropService.Capabilities.Description}");

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 启动参数：--show 打开主窗口，--settings 打开设置窗口，--demo 注入示例 U 盘
    /// （便于排障与脚本化验证）。
    /// </summary>
    private void HandleStartupArguments(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? [];

        if (args.Contains("--demo", StringComparer.OrdinalIgnoreCase))
        {
            _mainViewModel.AddDrive(DemoDriveCatalog.Drive);
            ShowMainWindow();
        }

        if (args.Contains("--show", StringComparer.OrdinalIgnoreCase))
            ShowMainWindow();

        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            OpenSettings();
    }

    private void ApplyTheme()
    {
        RequestedThemeVariant = SettingsStore.Current.Theme switch
        {
            AppTheme.Dark => ThemeVariant.Dark,
            AppTheme.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
    }

    // 背景或主题变化：需要重新设置原生窗口效果
    private void OnAppearanceChanged()
    {
        ApplyTheme();

        // 主题切换后 ActualThemeVariant 可能延后更新，等一帧再统一应用
        Dispatcher.UIThread.Post(ApplyAppearanceToAll, DispatcherPriority.Background);
    }

    // 只有底色浓度变化：不重设原生效果，避免拖动滑块时闪烁
    private void OnSurfaceChanged() => ApplySurfaceToAll();

    // 弹窗位置/时长变化：重排已有弹窗
    private void OnBehaviorChanged() => LayoutToasts();

    private void ApplyAppearanceToAll()
    {
        foreach (var window in AppearanceWindows())
        {
            if (window is UsbToastWindow toast)
                toast.ApplyToastAppearance();
            else
                BackdropService.Apply(window);
        }
    }

    private void ApplySurfaceToAll()
    {
        foreach (var window in AppearanceWindows())
            BackdropService.ApplySurface(window);
    }

    private IEnumerable<Window> AppearanceWindows()
    {
        if (_mainWindow is not null)
            yield return _mainWindow;

        if (_settingsWindow is not null)
            yield return _settingsWindow;

        if (_conflictWindow is not null)
            yield return _conflictWindow;

        foreach (var explorer in _explorerWindows.Values)
            yield return explorer;

        foreach (var toast in _usbToasts)
            yield return toast;
    }

    /// <summary>常驻程序兜底：记录 UI 线程 / 后台任务中未被处理的异常，避免进程无声退出。</summary>
    private void RegisterExceptionHandlers()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error("UI 线程未处理异常", e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("进程未处理异常", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>启动 U 盘插拔监听（M1：插入弹窗提示，点击一键打开）。</summary>
    private void StartUsbMonitor()
    {
        try
        {
            _usbMonitor = new UsbDeviceMonitor();
            _usbMonitor.DriveArrived += OnUsbDriveArrived;
            _usbMonitor.DriveRemoved += OnUsbDriveRemoved;
            _usbMonitor.Start();
        }
        catch (Exception ex)
        {
            Log.Error("启动 U 盘监听失败", ex);
        }
    }

    // U 盘插入：弹出提示窗，并同步到主窗口列表
    private void OnUsbDriveArrived(UsbDriveInfo drive)
    {
        _mainViewModel.AddDrive(drive);

        // 同一盘符若已有提示窗（例如快速重插），先收起旧的
        foreach (var existing in _usbToasts.Where(t => t.DriveLetter == drive.DriveLetter).ToArray())
            RemoveToast(existing);

        var toast = new UsbToastWindow(drive);
        toast.Closed += OnToastClosed;
        _usbToasts.Add(toast);
        LayoutToasts();
        toast.Show();
    }

    // U 盘拔出：收起对应提示窗与文件浏览器，并从主窗口列表移除
    private void OnUsbDriveRemoved(string driveLetter)
    {
        _mainViewModel.RemoveDrive(driveLetter);

        var toast = _usbToasts.FirstOrDefault(t => t.DriveLetter == driveLetter);
        if (toast is not null)
            RemoveToast(toast);

        if (_explorerWindows.Remove(driveLetter, out var explorer))
            explorer.Close();
    }

    private void OnToastClosed(object? sender, EventArgs e)
    {
        if (sender is not UsbToastWindow toast)
            return;

        toast.Closed -= OnToastClosed;

        // 只有当前仍在列表中时才处理（退场动画期间可能已被移除）
        if (_usbToasts.Remove(toast))
            LayoutToasts();
    }

    private void RemoveToast(UsbToastWindow toast)
    {
        if (!_usbToasts.Remove(toast))
            return;

        toast.Dismiss();
        LayoutToasts();
    }

    /// <summary>重排提示窗的叠放序号，避免关闭一个后留下空位或位置错乱。</summary>
    private void LayoutToasts()
    {
        for (var i = 0; i < _usbToasts.Count; i++)
            _usbToasts[i].SetSlot(i);
    }

    /// <summary>
    /// 第二个实例启动时：弹出「已在运行」提示窗口。
    /// 窗口已存在时只把它提到前面，避免重复叠窗。
    /// </summary>
    private void OnSecondInstanceDetected()
    {
        if (_conflictWindow is not null)
        {
            _conflictWindow.Activate();
            return;
        }

        var window = new InstanceConflictWindow();
        window.Closed += (_, _) => _conflictWindow = null;
        _conflictWindow = window;
        window.Show();
        window.Activate();
    }

    /// <summary>关闭当前实例并拉起新进程（新进程会等本进程退出后再接管单实例互斥体）。</summary>
    internal void RestartApplication()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            Log.Error("重新启动失败：无法获取当前程序路径");
            return;
        }

        // 先把尚未落盘的设置写完，新进程启动时会立即读取
        SettingsStore.SaveNow();
        _usbMonitor?.Dispose();

        try
        {
            Process.Start(new ProcessStartInfo(exe, $"--relaunch {Environment.ProcessId}")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("重新启动失败", ex);
            return;
        }

        Log.Info("用户选择重新启动程序");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (_mainWindow is not null)
                _mainWindow.AllowRealClose = true;
            desktop.Shutdown();
        }
    }

    // 托盘左键：恢复主窗口
    private void OnTrayIconClicked(object? sender, EventArgs e) => ShowMainWindow();

    // 托盘菜单：显示主窗口
    private void OnShowMainWindowClick(object? sender, EventArgs e) => ShowMainWindow();

    // 托盘菜单：打开设置
    private void OnSettingsMenuClick(object? sender, EventArgs e) => OpenSettings();

    // 托盘菜单：退出程序
    private void OnExitClick(object? sender, EventArgs e)
    {
        Log.Info("用户从托盘退出程序");

        SettingsStore.SaveNow();
        _usbMonitor?.Dispose();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (_mainWindow is not null)
                _mainWindow.AllowRealClose = true;
            desktop.Shutdown();
        }
    }

    internal void ShowMainWindow()
    {
        _mainWindow ??= new MainWindow
        {
            DataContext = _mainViewModel,
        };

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();

        // 打开窗口时校正一次列表，避免漏收事件或启动时盘尚未就绪
        _mainViewModel.RefreshDrives();
    }

    /// <summary>
    /// 打开某个 U 盘的内建文件浏览器；同一盘符只保留一个窗口。
    /// </summary>
    internal void OpenExplorer(UsbDriveInfo drive)
    {
        if (_explorerWindows.TryGetValue(drive.DriveLetter, out var existing))
        {
            if (!existing.IsVisible)
                existing.Show();

            existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new FileExplorerWindow(drive);
        window.Closed += (_, _) => _explorerWindows.Remove(drive.DriveLetter);
        _explorerWindows[drive.DriveLetter] = window;

        window.Show();
        window.Activate();
    }

    /// <summary>打开设置窗口（托盘菜单与主窗口齿轮共用同一个实例）。</summary>
    public void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            if (!_settingsWindow.IsVisible)
                _settingsWindow.Show();

            _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow { DataContext = new SettingsViewModel() };
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;

        window.Show();
        window.Activate();
    }
}
