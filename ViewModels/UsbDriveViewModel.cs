using System;
using System.Diagnostics;
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

    /// <summary>
    /// 打开 U 盘。M1 临时使用资源管理器，M2 将替换为内建文件管理器。
    /// </summary>
    [RelayCommand]
    private void OpenDrive()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Drive.RootPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // 点击瞬间 U 盘可能已被拔出，静默忽略
        }
    }
}
