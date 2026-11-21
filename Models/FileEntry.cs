using System;
using System.IO;

namespace FluentUSB.Models;

/// <summary>文件浏览器的排序列。</summary>
public enum FileSortColumn
{
    Name,
    Modified,
    Type,
    Size,
}

/// <summary>文件浏览器的分组依据。</summary>
public enum FileGroupBy
{
    None,
    Name,
    Modified,
    Type,
    Size,
}

/// <summary>
/// 文件浏览器中的一行（文件夹或文件）。
/// M2 阶段只展示示例盘数据，字段与真实文件系统的 DirectoryInfo / FileInfo 对齐。
/// </summary>
public sealed record FileEntry(string Name, bool IsDirectory, DateTime Modified, long Size)
{
    public string ModifiedText => Modified.ToString("yyyy/MM/dd HH:mm");

    public string SizeText => IsDirectory ? string.Empty : FormatBytes(Size);

    public string TypeText => IsDirectory ? "文件夹" : DescribeType(Name);

    /// <summary>Segoe Fluent Icons 图标码位，按文件类别区分。</summary>
    public string IconGlyph
    {
        get
        {
            if (IsDirectory)
                return "\uE8B7";

            return Path.GetExtension(Name).ToLowerInvariant() switch
            {
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" => "\uEB9F",
                ".mp4" or ".mkv" or ".mov" or ".avi" => "\uE714",
                ".mp3" or ".flac" or ".wav" or ".m4a" => "\uE8D6",
                ".zip" or ".rar" or ".7z" => "\uE7B8",
                ".exe" or ".msi" => "\uE7EF",
                _ => "\uE8A5",
            };
        }
    }

    private static string DescribeType(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension switch
        {
            "" => "文件",
            ".txt" or ".md" => "文本文档",
            ".pdf" => "PDF 文档",
            ".doc" or ".docx" => "Word 文档",
            ".xls" or ".xlsx" or ".csv" => "Excel 工作表",
            ".ppt" or ".pptx" => "PowerPoint 演示文稿",
            ".zip" or ".rar" or ".7z" => "压缩文件",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" => "图片",
            ".mp4" or ".mkv" or ".mov" or ".avi" => "视频",
            ".mp3" or ".flac" or ".wav" or ".m4a" => "音频",
            ".exe" => "应用程序",
            ".msi" => "Windows 安装包",
            _ => $"{extension.TrimStart('.').ToUpperInvariant()} 文件",
        };
    }

    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    private static string FormatBytes(long bytes)
    {
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < ByteUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {ByteUnits[unit]}";
    }
}
