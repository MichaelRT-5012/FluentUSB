using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentUSB.Models;
using FluentUSB.Services;

namespace FluentUSB.ViewModels;

/// <summary>
/// 主窗口视图模型：展示当前已连接的 U 盘，并随插拔事件实时更新。
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    public string AppName => "FluentUSB";

    public string Subtitle => "U 盘助手";

    /// <summary>已连接的 U 盘。</summary>
    public ObservableCollection<UsbDriveViewModel> DriveList { get; } = [];

    /// <summary>有 U 盘时显示列表，否则显示空状态。</summary>
    [ObservableProperty]
    private bool _hasDrives;

    /// <summary>连点主窗口 5 次后显示示例 U 盘入口（调试用）。</summary>
    [ObservableProperty]
    private bool _isDemoRevealed;

    /// <summary>右上角监听状态文案。</summary>
    public string StatusText =>
        HasDrives ? $"已连接 {DriveList.Count} 个 U 盘" : "尚未连接 U 盘";

    /// <summary>
    /// 重新枚举当前在位的 U 盘并校正列表。
    /// 枚举驱动器会阻塞，放到后台线程，完成后回到 UI 线程更新集合。
    /// 启动时与每次打开主窗口时调用，避免漏掉启动瞬间尚未就绪的盘。
    /// </summary>
    public void RefreshDrives()
    {
        _ = RefreshDrivesAsync();
    }

    /// <summary>U 盘插入（挂载完成后由 UsbDeviceMonitor 调用）。</summary>
    public void AddDrive(UsbDriveInfo drive)
    {
        var existing = DriveList.FirstOrDefault(d => d.DriveLetter == drive.DriveLetter);
        if (existing is not null)
            DriveList.Remove(existing);

        DriveList.Add(new UsbDriveViewModel(drive));
        UpdateState();
    }

    /// <summary>U 盘拔出。</summary>
    public void RemoveDrive(string driveLetter)
    {
        var existing = DriveList.FirstOrDefault(d => d.DriveLetter == driveLetter);
        if (existing is not null)
            DriveList.Remove(existing);

        UpdateState();
    }

    /// <summary>显示示例 U 盘入口（主窗口连点 5 次触发）。</summary>
    public void RevealDemo() => IsDemoRevealed = true;

    /// <summary>把内置示例 U 盘加入列表，便于验证文件浏览器。</summary>
    [RelayCommand]
    private void AddDemoDrive() => AddDrive(DemoDriveCatalog.Drive);

    private async Task RefreshDrivesAsync()
    {
        try
        {
            var drives = await Task.Run(UsbDriveProbe.EnumerateReadyUsbDrives);
            Dispatcher.UIThread.Post(() =>
            {
                // 只补齐缺失的盘；拔出由 UsbDeviceMonitor 事件负责移除，
                // 不能在此删除，否则会误删"枚举开始后刚插入"的盘
                foreach (var drive in drives)
                {
                    if (DriveList.All(v => v.DriveLetter != drive.DriveLetter))
                        DriveList.Add(new UsbDriveViewModel(drive));
                }

                UpdateState();
            });
        }
        catch (Exception ex)
        {
            Log.Error("刷新已连接 U 盘失败", ex);
        }
    }

    private void UpdateState()
    {
        HasDrives = DriveList.Count > 0;
        OnPropertyChanged(nameof(StatusText));
    }
}
