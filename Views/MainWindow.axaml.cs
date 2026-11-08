using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FluentUSB.Services;

namespace FluentUSB.Views;

public partial class MainWindow : Window, IAppearanceHost
{
    /// <summary>为 true 时允许窗口真正关闭（仅托盘「退出」时设置）。</summary>
    public bool AllowRealClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        // 窗口显示后才有句柄，此时才能应用原生背景效果
        Opened += (_, _) => BackdropService.Apply(this);
        ActualThemeVariantChanged += (_, _) => BackdropService.Apply(this);
    }

    public Border? SurfaceHost => Surface;

    private void OnSettingsClick(object? sender, RoutedEventArgs e) =>
        (Application.Current as App)?.OpenSettings();

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
