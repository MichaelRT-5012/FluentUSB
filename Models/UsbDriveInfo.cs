namespace FluentUSB.Models;

/// <summary>
/// U 盘（可移动卷）信息快照，在插入并挂载完成后生成。
/// </summary>
/// <param name="DriveLetter">盘符，如 "D:"。</param>
/// <param name="RootPath">根目录，如 "D:\"。</param>
/// <param name="VolumeLabel">卷标（可能为空）。</param>
/// <param name="DriveFormat">文件系统，如 NTFS / exFAT / FAT32。</param>
/// <param name="TotalSize">总容量（字节）。</param>
/// <param name="FreeSpace">可用空间（字节）。</param>
public sealed record UsbDriveInfo(
    string DriveLetter,
    string RootPath,
    string VolumeLabel,
    string DriveFormat,
    long TotalSize,
    long FreeSpace)
{
    /// <summary>展示名称：优先卷标，无卷标时兜底。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(VolumeLabel) ? "USB 磁盘" : VolumeLabel;

    /// <summary>容量摘要，供弹窗展示，如 "总共 14.9 GB · 可用 3.2 GB"。</summary>
    public string SizeSummary => $"总共 {FormatBytes(TotalSize)} · 可用 {FormatBytes(FreeSpace)}";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }
}
