using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentUSB.Models;

namespace FluentUSB.ViewModels;

/// <summary>
/// 主窗口视图模型：展示当前已连接的 U 盘，并随插拔事件实时更新。
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel()
    {
        foreach (var drive in EnumerateRemovableDrives())
            DriveList.Add(new UsbDriveViewModel(drive));
        UpdateState();
    }

    public string AppName => "FluentUSB";

    public string Subtitle => "U 盘助手";

    /// <summary>已连接的 U 盘。</summary>
    public ObservableCollection<UsbDriveViewModel> DriveList { get; } = [];

    /// <summary>有 U 盘时显示列表，否则显示空状态。</summary>
    [ObservableProperty]
    private bool _hasDrives;

    /// <summary>右上角监听状态文案。</summary>
    public string StatusText =>
        HasDrives ? $"正在监听 · 已连接 {DriveList.Count} 个 U 盘" : "正在监听 U 盘插入";

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

    private void UpdateState()
    {
        HasDrives = DriveList.Count > 0;
        OnPropertyChanged(nameof(StatusText));
    }

    private static UsbDriveInfo[] EnumerateRemovableDrives() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Removable)
            .Select(TryCreate)
            .OfType<UsbDriveInfo>()
            .ToArray();

    /// <summary>读取单个驱动器信息；未就绪（如空读卡器）时返回 null。</summary>
    private static UsbDriveInfo? TryCreate(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady || drive.TotalSize <= 0)
                return null;

            return new UsbDriveInfo(
                DriveLetter: char.ToUpperInvariant(drive.Name[0]) + ":",
                RootPath: drive.RootDirectory.FullName,
                VolumeLabel: drive.VolumeLabel,
                DriveFormat: drive.DriveFormat,
                TotalSize: drive.TotalSize,
                FreeSpace: drive.AvailableFreeSpace);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
