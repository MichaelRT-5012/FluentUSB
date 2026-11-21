using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentUSB.Models;

namespace FluentUSB.ViewModels;

/// <summary>
/// 分组标题行：显示分组名与项目数，并持有该分组的文件行。
/// 展开/收起只改变自身状态，由 XAML 的高度过渡完成动画，不需要重建整个列表。
/// </summary>
public sealed partial class FileGroupRow : ObservableObject
{
    /// <summary>与文件行模板中的固定行高保持一致。</summary>
    private const double RowHeight = 38;

    public FileGroupRow(string key, string title)
    {
        Key = key;
        Title = title;
    }

    public string Key { get; }

    public string Title { get; }

    public ObservableCollection<FileEntry> Entries { get; } = [];

    public string CountText => $"{Entries.Count} 项";

    /// <summary>展开后内容区的目标高度（文件行数 × 行高）。</summary>
    public double ExpandedHeight => IsExpanded ? Entries.Count * RowHeight : 0;

    public double ContentOpacity => IsExpanded ? 1 : 0;

    [ObservableProperty]
    private bool _isExpanded = true;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandedHeight));
        OnPropertyChanged(nameof(ContentOpacity));
    }
}
