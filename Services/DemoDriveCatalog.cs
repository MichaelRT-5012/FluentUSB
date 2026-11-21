using System;
using System.Collections.Generic;
using FluentUSB.Models;

namespace FluentUSB.Services;

/// <summary>
/// 调试用的示例 U 盘：不触碰真实磁盘，固定提供一份根目录内容。
/// 触发方式：主窗口连点 5 次后点出现的示例 U 盘图标，或启动参数 --demo。
/// </summary>
internal static class DemoDriveCatalog
{
    /// <summary>示例盘信息（盘符为 DEMO:，不会与真实盘冲突）。</summary>
    public static readonly UsbDriveInfo Drive = new(
        DriveLetter: "DEMO:",
        RootPath: @"DEMO:\",
        VolumeLabel: "示例 U 盘",
        DriveFormat: "exFAT",
        TotalSize: 15_902_400_000,
        FreeSpace: 3_412_000_000,
        IsDemo: true);

    /// <summary>示例盘根目录条目：文件夹在前，文件足够多以便验证滚动与排序。</summary>
    public static IReadOnlyList<FileEntry> CreateRootEntries() =>
    [
        new("照片", true, At(2026, 11, 18, 21, 4), 0),
        new("文档", true, At(2026, 11, 17, 9, 32), 0),
        new("音乐", true, At(2026, 10, 30, 22, 18), 0),
        new("视频", true, At(2026, 11, 12, 14, 46), 0),
        new("安装包", true, At(2026, 9, 5, 11, 2), 0),
        new("备份", true, At(2026, 11, 20, 18, 25), 0),
        new("使用说明.pdf", false, At(2026, 11, 20, 19, 3), 1_248_576),
        new("从这里开始.txt", false, At(2026, 11, 20, 19, 5), 2_048),
        new("旅行清单.xlsx", false, At(2026, 10, 8, 8, 41), 49_152),
        new("项目备份.zip", false, At(2026, 11, 19, 23, 12), 2_576_980_480),
        new("演示视频.mp4", false, At(2026, 11, 12, 14, 46), 134_217_728),
        new("桌面壁纸.png", false, At(2026, 10, 21, 20, 30), 3_984_588),
        new("会议录音.m4a", false, At(2026, 11, 6, 16, 8), 13_213_696),
        new("FluentUSB 安装包.msi", false, At(2026, 9, 5, 11, 2), 19_293_184),
        new("驱动工具.exe", false, At(2026, 8, 17, 13, 55), 7_024_640),
        new("读书笔记.md", false, At(2026, 11, 15, 22, 37), 3_072),
        new("财务报表 2026.xlsx", false, At(2026, 11, 18, 10, 12), 88_064),
        new("logo.svg", false, At(2026, 7, 29, 17, 44), 24_576),
    ];

    private static DateTime At(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Local);
}
