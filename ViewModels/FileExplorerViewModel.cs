using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentUSB.Models;
using FluentUSB.Services;

namespace FluentUSB.ViewModels;

/// <summary>列表重建的原因，用于让窗口选择不同的动画。</summary>
public enum FileListChangeKind
{
    Initial,
    SortColumn,
    SortDirection,
    Grouping,
}

/// <summary>
/// 内建文件浏览器（M2）：列出 U 盘根目录条目，支持按列排序与分组。
/// 现阶段只接示例盘数据，真实磁盘读取留到下一阶段。
/// </summary>
public partial class FileExplorerViewModel : ViewModelBase
{
    // 固定分组顺序，避免"今天/昨天"等分组的显示顺序随排序方向乱跳
    private static readonly string[] DateGroupOrder = ["今天", "昨天", "本周", "本月", "今年", "更早"];
    private static readonly string[] SizeGroupOrder = ["文件夹", "空", "小", "中", "大", "超大"];

    private readonly List<FileEntry> _entries;
    private readonly Comparer<FileEntry> _entryComparer;
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);

    public FileExplorerViewModel(UsbDriveInfo drive)
    {
        Drive = drive;
        _entries = drive.IsDemo ? [.. DemoDriveCatalog.CreateRootEntries()] : [];
        _entryComparer = Comparer<FileEntry>.Create(CompareEntries);
        EmptyHint = drive.IsDemo ? "这个 U 盘里还没有文件" : "读取真实 U 盘内容将在下一阶段接入";

        Rebuild(FileListChangeKind.Initial);
    }

    /// <summary>列表重建完成，参数说明本次重建的原因。</summary>
    public event Action<FileListChangeKind>? Rebuilt;

    public UsbDriveInfo Drive { get; }

    public string DriveName => Drive.DisplayName;

    public string DriveLetter => Drive.DriveLetter;

    public string PathText => Drive.RootPath;

    public string EmptyHint { get; }

    /// <summary>列表实际渲染的行：文件行或分组标题行。</summary>
    public ObservableCollection<object> Items { get; } = [];

    public bool HasItems => _entries.Count > 0;

    public string ItemCountText => $"{_entries.Count} 个项目";

    [ObservableProperty]
    private FileSortColumn _sortColumn = FileSortColumn.Name;

    [ObservableProperty]
    private bool _sortDescending;

    [ObservableProperty]
    private FileGroupBy _groupBy = FileGroupBy.None;

    /// <summary>各列宽度：表头与文件行共用，保证列边界完全对齐，并支持拖动表头调整。</summary>
    [ObservableProperty]
    private GridLength _nameColumnWidth = new(1, GridUnitType.Star);

    [ObservableProperty]
    private GridLength _modifiedColumnWidth = new(160);

    [ObservableProperty]
    private GridLength _typeColumnWidth = new(130);

    [ObservableProperty]
    private GridLength _sizeColumnWidth = new(100);

    public bool IsNameSorted => SortColumn == FileSortColumn.Name;

    public bool IsModifiedSorted => SortColumn == FileSortColumn.Modified;

    public bool IsTypeSorted => SortColumn == FileSortColumn.Type;

    public bool IsSizeSorted => SortColumn == FileSortColumn.Size;

    public bool IsAscending => !SortDescending;

    public bool IsDescending => SortDescending;

    public bool IsGroupedByNone => GroupBy == FileGroupBy.None;

    public bool IsGroupedByName => GroupBy == FileGroupBy.Name;

    public bool IsGroupedByModified => GroupBy == FileGroupBy.Modified;

    public bool IsGroupedByType => GroupBy == FileGroupBy.Type;

    public bool IsGroupedBySize => GroupBy == FileGroupBy.Size;

    public string NameSortGlyph => GlyphFor(FileSortColumn.Name);

    public string ModifiedSortGlyph => GlyphFor(FileSortColumn.Modified);

    public string TypeSortGlyph => GlyphFor(FileSortColumn.Type);

    public string SizeSortGlyph => GlyphFor(FileSortColumn.Size);

    /// <summary>点击表头：同一列切换升降序，换列时从升序开始。</summary>
    [RelayCommand]
    private void SortBy(string? column)
    {
        if (!Enum.TryParse<FileSortColumn>(column, out var target))
            return;

        if (SortColumn == target)
        {
            SortDescending = !SortDescending;
            Rebuild(FileListChangeKind.SortDirection);
        }
        else
        {
            SortColumn = target;
            SortDescending = false;
            Rebuild(FileListChangeKind.SortColumn);
        }
    }

    [RelayCommand]
    private void SortAscending()
    {
        if (!SortDescending)
            return;

        SortDescending = false;
        Rebuild(FileListChangeKind.SortDirection);
    }

    [RelayCommand]
    private void SortDescendingOrder()
    {
        if (SortDescending)
            return;

        SortDescending = true;
        Rebuild(FileListChangeKind.SortDirection);
    }

    /// <summary>菜单「查看 → 分组依据」。</summary>
    [RelayCommand]
    private void SetGroupBy(string? option)
    {
        if (Enum.TryParse<FileGroupBy>(option, out var target))
            GroupBy = target;
    }

    /// <summary>折叠/展开单个分组：只切换该分组的状态，由视图的高度过渡完成动画。</summary>
    public void ToggleGroup(string key)
    {
        var group = Items.OfType<FileGroupRow>().FirstOrDefault(g => g.Key == key);
        if (group is null)
            return;

        group.IsExpanded = !group.IsExpanded;

        if (group.IsExpanded)
            _collapsedGroups.Remove(key);
        else
            _collapsedGroups.Add(key);
    }

    [RelayCommand]
    private void ExpandAllGroups()
    {
        _collapsedGroups.Clear();

        foreach (var group in Items.OfType<FileGroupRow>())
            group.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAllGroups()
    {
        if (GroupBy == FileGroupBy.None)
            return;

        foreach (var group in Items.OfType<FileGroupRow>())
        {
            group.IsExpanded = false;
            _collapsedGroups.Add(group.Key);
        }
    }

    private void Rebuild(FileListChangeKind kind)
    {
        Items.Clear();

        if (GroupBy == FileGroupBy.None)
        {
            foreach (var entry in SortedEntries(_entries))
                Items.Add(entry);
        }
        else
        {
            foreach (var bucket in BuildGroups())
            {
                var group = new FileGroupRow(bucket.Key, bucket.Title)
                {
                    IsExpanded = !_collapsedGroups.Contains(bucket.Key),
                };

                foreach (var entry in SortedEntries(bucket.Entries))
                    group.Entries.Add(entry);

                Items.Add(group);
            }
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ItemCountText));

        Rebuilt?.Invoke(kind);
    }

    private List<FileEntry> SortedEntries(IEnumerable<FileEntry> source)
    {
        var list = new List<FileEntry>(source);
        list.Sort(_entryComparer);
        return list;
    }

    private sealed record GroupBucket(string Key, string Title, List<FileEntry> Entries);

    private List<GroupBucket> BuildGroups()
    {
        var buckets = new Dictionary<string, List<FileEntry>>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            var title = GroupTitleOf(entry);
            if (!buckets.TryGetValue(title, out var list))
                buckets[title] = list = [];

            list.Add(entry);
        }

        var order = GroupBy switch
        {
            FileGroupBy.Modified => DateGroupOrder.Where(buckets.ContainsKey),
            FileGroupBy.Size => SizeGroupOrder.Where(buckets.ContainsKey),
            FileGroupBy.Name => buckets.Keys.OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase),
            FileGroupBy.Type => buckets.Keys.OrderBy(k => k == "文件夹" ? string.Empty : k,
                StringComparer.CurrentCultureIgnoreCase),
            _ => buckets.Keys.Order(StringComparer.CurrentCultureIgnoreCase),
        };

        return order.Select(key => new GroupBucket(key, key, buckets[key])).ToList();
    }

    private string GroupTitleOf(FileEntry entry) => GroupBy switch
    {
        FileGroupBy.Name => NameGroupOf(entry.Name),
        FileGroupBy.Modified => DateGroupOf(entry.Modified),
        FileGroupBy.Type => entry.TypeText,
        FileGroupBy.Size => SizeGroupOf(entry),
        _ => string.Empty,
    };

    /// <summary>名称分组：数字归入「0 - 9」，其余取首字符（大写）。</summary>
    private static string NameGroupOf(string name)
    {
        if (name.Length == 0)
            return "#";

        var first = name[0];
        return char.IsDigit(first) ? "0 - 9" : char.ToUpperInvariant(first).ToString();
    }

    private static string DateGroupOf(DateTime modified)
    {
        var today = DateTime.Today;

        if (modified >= today)
            return "今天";
        if (modified >= today.AddDays(-1))
            return "昨天";

        // 周一为一周起点
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if (modified >= weekStart)
            return "本周";

        if (modified.Year == today.Year && modified.Month == today.Month)
            return "本月";
        if (modified.Year == today.Year)
            return "今年";

        return "更早";
    }

    private static string SizeGroupOf(FileEntry entry)
    {
        if (entry.IsDirectory)
            return "文件夹";

        return entry.Size switch
        {
            <= 0 => "空",
            < 1_048_576 => "小",
            < 16_777_216 => "中",
            < 134_217_728 => "大",
            _ => "超大",
        };
    }

    /// <summary>文件夹恒在前，同组内按当前列排序；名称作为次级排序键。</summary>
    private int CompareEntries(FileEntry a, FileEntry b)
    {
        if (a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;

        var result = SortColumn switch
        {
            FileSortColumn.Modified => a.Modified.CompareTo(b.Modified),
            FileSortColumn.Type => string.Compare(a.TypeText, b.TypeText, StringComparison.CurrentCultureIgnoreCase),
            FileSortColumn.Size => a.Size.CompareTo(b.Size),
            _ => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
        };

        // 同值时按名称排序，避免内容看起来随机
        if (result == 0 && SortColumn != FileSortColumn.Name)
            result = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);

        return SortDescending ? -result : result;
    }

    private string GlyphFor(FileSortColumn column) =>
        SortColumn != column ? string.Empty : SortDescending ? "\uE70D" : "\uE70E";

    partial void OnSortColumnChanged(FileSortColumn value) => RaiseSortIndicators();

    partial void OnSortDescendingChanged(bool value) => RaiseSortIndicators();

    partial void OnGroupByChanged(FileGroupBy value)
    {
        RaiseGroupIndicators();
        Rebuild(FileListChangeKind.Grouping);
    }

    private void RaiseSortIndicators()
    {
        OnPropertyChanged(nameof(IsNameSorted));
        OnPropertyChanged(nameof(IsModifiedSorted));
        OnPropertyChanged(nameof(IsTypeSorted));
        OnPropertyChanged(nameof(IsSizeSorted));
        OnPropertyChanged(nameof(IsAscending));
        OnPropertyChanged(nameof(IsDescending));
        OnPropertyChanged(nameof(NameSortGlyph));
        OnPropertyChanged(nameof(ModifiedSortGlyph));
        OnPropertyChanged(nameof(TypeSortGlyph));
        OnPropertyChanged(nameof(SizeSortGlyph));
    }

    private void RaiseGroupIndicators()
    {
        OnPropertyChanged(nameof(IsGroupedByNone));
        OnPropertyChanged(nameof(IsGroupedByName));
        OnPropertyChanged(nameof(IsGroupedByModified));
        OnPropertyChanged(nameof(IsGroupedByType));
        OnPropertyChanged(nameof(IsGroupedBySize));
    }
}
