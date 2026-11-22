using System;
using System.Collections.Generic;
using FluentUSB.Models;

namespace FluentUSB.Services;

/// <summary>
/// 调试用的示例 U 盘：不触碰真实磁盘，按路径提供固定的目录内容。
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

    /// <summary>按路径返回示例目录内容；未知路径返回空目录。</summary>
    public static IReadOnlyList<FileEntry> CreateEntries(string path) => path switch
    {
        @"DEMO:\" => RootEntries(),
        @"DEMO:\照片" =>
        [
            new(@"DEMO:\照片\海边日落.jpg", "海边日落.jpg", false, At(2026, 11, 18, 20, 46), 4_194_304),
            new(@"DEMO:\照片\城市夜景.jpg", "城市夜景.jpg", false, At(2026, 10, 26, 22, 12), 3_145_728),
            new(@"DEMO:\照片\截图 2026-11-19.png", "截图 2026-11-19.png", false, At(2026, 11, 19, 9, 8), 862_208),
        ],
        @"DEMO:\文档" =>
        [
            new(@"DEMO:\文档\项目计划.docx", "项目计划.docx", false, At(2026, 11, 16, 15, 30), 78_848),
            new(@"DEMO:\文档\需求说明.pdf", "需求说明.pdf", false, At(2026, 11, 17, 10, 2), 1_572_864),
            new(@"DEMO:\文档\待办事项.txt", "待办事项.txt", false, At(2026, 11, 21, 8, 15), 1_024),
        ],
        @"DEMO:\音乐" =>
        [
            new(@"DEMO:\音乐\清晨.mp3", "清晨.mp3", false, At(2026, 10, 3, 7, 20), 8_388_608),
            new(@"DEMO:\音乐\夜航.flac", "夜航.flac", false, At(2026, 9, 27, 23, 4), 41_943_040),
        ],
        @"DEMO:\视频" =>
        [
            new(@"DEMO:\视频\产品演示.mp4", "产品演示.mp4", false, At(2026, 11, 12, 14, 46), 236_223_488),
        ],
        @"DEMO:\安装包" =>
        [
            new(@"DEMO:\安装包\FluentUSB 安装包.msi", "FluentUSB 安装包.msi", false, At(2026, 9, 5, 11, 2), 19_293_184),
            new(@"DEMO:\安装包\驱动工具.exe", "驱动工具.exe", false, At(2026, 8, 17, 13, 55), 7_024_640),
        ],
        @"DEMO:\备份" =>
        [
            new(@"DEMO:\备份\项目备份 2026-11.zip", "项目备份 2026-11.zip", false, At(2026, 11, 19, 23, 12), 2_576_980_480),
            new(@"DEMO:\备份\旧电脑资料.7z", "旧电脑资料.7z", false, At(2026, 8, 2, 18, 30), 8_589_934_592),
        ],
        _ => [],
    };

    /// <summary>示例盘根目录条目：文件夹在前，文件足够多以便验证滚动与排序。</summary>
    private static IReadOnlyList<FileEntry> RootEntries() =>
    [
        new(@"DEMO:\照片", "照片", true, At(2026, 11, 18, 21, 4), 0),
        new(@"DEMO:\文档", "文档", true, At(2026, 11, 17, 9, 32), 0),
        new(@"DEMO:\音乐", "音乐", true, At(2026, 10, 30, 22, 18), 0),
        new(@"DEMO:\视频", "视频", true, At(2026, 11, 12, 14, 46), 0),
        new(@"DEMO:\安装包", "安装包", true, At(2026, 9, 5, 11, 2), 0),
        new(@"DEMO:\备份", "备份", true, At(2026, 11, 20, 18, 25), 0),
        new(@"DEMO:\使用说明.pdf", "使用说明.pdf", false, At(2026, 11, 20, 19, 3), 1_248_576),
        new(@"DEMO:\从这里开始.txt", "从这里开始.txt", false, At(2026, 11, 20, 19, 5), 2_048),
        new(@"DEMO:\旅行清单.xlsx", "旅行清单.xlsx", false, At(2026, 10, 8, 8, 41), 49_152),
        new(@"DEMO:\项目备份.zip", "项目备份.zip", false, At(2026, 11, 19, 23, 12), 2_576_980_480),
        new(@"DEMO:\演示视频.mp4", "演示视频.mp4", false, At(2026, 11, 12, 14, 46), 134_217_728),
        new(@"DEMO:\桌面壁纸.png", "桌面壁纸.png", false, At(2026, 10, 21, 20, 30), 3_984_588),
        new(@"DEMO:\会议录音.m4a", "会议录音.m4a", false, At(2026, 11, 6, 16, 8), 13_213_696),
        new(@"DEMO:\FluentUSB 安装包.msi", "FluentUSB 安装包.msi", false, At(2026, 9, 5, 11, 2), 19_293_184),
        new(@"DEMO:\驱动工具.exe", "驱动工具.exe", false, At(2026, 8, 17, 13, 55), 7_024_640),
        new(@"DEMO:\读书笔记.md", "读书笔记.md", false, At(2026, 11, 15, 22, 37), 3_072),
        new(@"DEMO:\财务报表 2026.xlsx", "财务报表 2026.xlsx", false, At(2026, 11, 18, 10, 12), 88_064),
        new(@"DEMO:\logo.svg", "logo.svg", false, At(2026, 7, 29, 17, 44), 24_576),
    ];

    private static DateTime At(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Local);
}
