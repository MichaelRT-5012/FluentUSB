using Avalonia.Controls;

namespace FluentUSB.Views;

public partial class MainWindow : Window
{
    /// <summary>为 true 时允许窗口真正关闭（仅托盘「退出」时设置）。</summary>
    public bool AllowRealClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        // 透明度级别集中由 UiOptions 控制（预留亚克力切换）
        TransparencyLevelHint = UiOptions.TransparencyHint;
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
