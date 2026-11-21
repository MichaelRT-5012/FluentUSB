using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FluentUSB.Services;

namespace FluentUSB.Views;

/// <summary>
/// 第二个实例启动时，由已运行的实例弹出的提示窗口：
/// 用户可以选择重启程序、打开现有实例的主窗口/设置，或取消。
/// </summary>
public partial class InstanceConflictWindow : Window, IAppearanceHost
{
    public InstanceConflictWindow()
    {
        InitializeComponent();

        Opened += (_, _) => BackdropService.Apply(this);
        ActualThemeVariantChanged += (_, _) => BackdropService.Apply(this);
    }

    public Border? SurfaceHost => Surface;

    private App? App => Application.Current as App;

    private void OnRestartClick(object? sender, RoutedEventArgs e)
    {
        Close();
        App?.RestartApplication();
    }

    private void OnShowMainClick(object? sender, RoutedEventArgs e)
    {
        Close();
        App?.ShowMainWindow();
    }

    private void OnOpenSettingsClick(object? sender, RoutedEventArgs e)
    {
        Close();
        App?.OpenSettings();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
