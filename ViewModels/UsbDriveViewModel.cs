using Avalonia;
using CommunityToolkit.Mvvm.Input;
using FluentUSB.Models;

namespace FluentUSB.ViewModels;

/// <summary>单个 U 盘的展示模型，供插入弹窗与主窗口列表共用。</summary>
public partial class UsbDriveViewModel : ViewModelBase
{
    public UsbDriveViewModel(UsbDriveInfo drive) => Drive = drive;

    public UsbDriveInfo Drive { get; }

    public string DisplayName => Drive.DisplayName;

    public string DriveLetter => Drive.DriveLetter;

    public string SizeSummary => Drive.SizeSummary;

    public string FormatText => Drive.DriveFormat;

    /// <summary>打开 U 盘：M2 起进入内建文件浏览器，不再跳转系统资源管理器。</summary>
    [RelayCommand]
    private void OpenDrive()
    {
        if (Application.Current is App app)
            app.OpenExplorer(Drive);
    }
}
