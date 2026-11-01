namespace FluentUSB.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public string AppName { get; } = "FluentUSB";

    public string Description { get; } =
        "常驻 U 盘助手：检测到 U 盘插入后自动扫描，按最新日期列出文件，" +
        "一键复制到预设位置并打开。（功能开发中，当前为 M0 占位界面）";
}
