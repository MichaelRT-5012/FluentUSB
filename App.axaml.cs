using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FluentUSB.ViewModels;
using FluentUSB.Views;
using System;

namespace FluentUSB;

public partial class App : Application
{
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

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // 托盘左键：恢复主窗口
    private void OnTrayIconClicked(object? sender, EventArgs e) => ShowMainWindow();

    // 托盘菜单：显示主窗口
    private void OnShowMainWindowClick(object? sender, EventArgs e) => ShowMainWindow();

    // 托盘菜单：退出程序
    private void OnExitClick(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is MainWindow mainWindow)
                mainWindow.AllowRealClose = true;
            desktop.Shutdown();
        }
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        desktop.MainWindow ??= new MainWindow
        {
            DataContext = new MainWindowViewModel(),
        };

        var window = desktop.MainWindow;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
