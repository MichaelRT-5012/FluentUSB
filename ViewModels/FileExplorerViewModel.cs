using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentUSB.Models;
using FluentUSB.Services;

namespace FluentUSB.ViewModels;

/// <summary>文件操作进度快照：百分比、当前条目、项目数、字节数与实时速度。</summary>
public sealed record FileOperationProgress(
    double Percent,
    string Detail,
    int DoneItems,
    int TotalItems,
    long DoneBytes,
    long TotalBytes,
    double BytesPerSecond);

/// <summary>列表重建的原因，用于让窗口选择不同的动画。</summary>
public enum FileListChangeKind
{
    Initial,
    Navigation,
    SortColumn,
    SortDirection,
    Grouping,
}

/// <summary>
/// 内建文件浏览器：读取真实 U 盘目录，支持排序、分组、导航与常用文件操作。
/// 示例盘（DEMO:）使用内置数据，只读，不执行真实文件操作。
/// </summary>
public partial class FileExplorerViewModel : ViewModelBase
{
    // 固定分组顺序，避免"今天/昨天"等分组的显示顺序随排序方向乱跳
    private static readonly string[] DateGroupOrder = ["今天", "昨天", "本周", "本月", "今年", "更早"];
    private static readonly string[] SizeGroupOrder = ["文件夹", "空", "小", "中", "大", "超大"];

    private List<FileEntry> _entries = [];
    private readonly List<FileEntry> _displayEntries = [];
    private readonly Comparer<FileEntry> _entryComparer;
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);
    private readonly Stack<string> _backHistory = new();
    private readonly List<string> _clipboardPaths = [];

    private string _currentPath;
    private bool _clipboardIsCut;
    private bool _loadFailed;
    private FileEntry? _selectionAnchor;
    private FileEntry? _renameEntry;
    private CancellationTokenSource? _operationCts;

    // 暂停/继续复制的闸门：Reset 时后台线程阻塞，Set 后继续
    private ManualResetEventSlim? _operationGate;

    public FileExplorerViewModel(UsbDriveInfo drive)
    {
        Drive = drive;
        _currentPath = drive.RootPath;
        _entryComparer = Comparer<FileEntry>.Create(CompareEntries);

        LoadDirectory(FileListChangeKind.Initial);
    }

    /// <summary>列表重建完成，参数说明本次重建的原因。</summary>
    public event Action<FileListChangeKind>? Rebuilt;

    public UsbDriveInfo Drive { get; }

    public string DriveName => Drive.DisplayName;

    public string DriveLetter => Drive.DriveLetter;

    public string PathText => _currentPath;

    public string EmptyHint => _loadFailed ? "无法访问此文件夹" : "此文件夹为空";

    public bool HasItems => _entries.Count > 0;

    public string ItemCountText => $"{_entries.Count} 个项目";

    public bool CanGoUp => ParentPathOf(_currentPath) is not null;

    public bool CanGoBack => _backHistory.Count > 0;

    /// <summary>示例盘不执行真实文件操作。</summary>
    private bool IsRealDrive => !Drive.IsDemo;

    /// <summary>列表实际渲染的行：文件行或分组标题行。</summary>
    public ObservableCollection<object> Items { get; } = [];

    [ObservableProperty]
    private FileSortColumn _sortColumn = FileSortColumn.Name;

    [ObservableProperty]
    private bool _sortDescending;

    [ObservableProperty]
    private FileGroupBy _groupBy = FileGroupBy.None;

    /// <summary>删除确认浮层。</summary>
    [ObservableProperty]
    private bool _isDeletePromptVisible;

    [ObservableProperty]
    private string _deletePromptText = string.Empty;

    /// <summary>重命名浮层。</summary>
    [ObservableProperty]
    private bool _isRenamePromptVisible;

    [ObservableProperty]
    private string _renameText = string.Empty;

    /// <summary>长时间文件操作（复制/移动/删除）的进度浮层。</summary>
    [ObservableProperty]
    private bool _isOperationInProgress;

    [ObservableProperty]
    private bool _isOperationIndeterminate;

    [ObservableProperty]
    private string _operationTitle = "正在复制";

    [ObservableProperty]
    private string _operationDetail = string.Empty;

    [ObservableProperty]
    private double _operationPercent;

    [ObservableProperty]
    private string _operationItemsText = string.Empty;

    [ObservableProperty]
    private string _operationSizeText = string.Empty;

    [ObservableProperty]
    private string _operationSpeedText = string.Empty;

    [ObservableProperty]
    private bool _canCancelOperation;

    [ObservableProperty]
    private bool _canPauseOperation;

    [ObservableProperty]
    private bool _isOperationPaused;

    public string OperationPauseText => IsOperationPaused ? "继续" : "暂停";

    public string OperationPercentText =>
        IsOperationIndeterminate ? "—" : $"{OperationPercent:0}%";

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

    // ---------- 导航 ----------

    [RelayCommand]
    private void GoBack()
    {
        TryGoBack();
    }

    /// <summary>返回上一级目录；供窗口先播放退场动画再执行。</summary>
    public bool TryGoBack()
    {
        if (_backHistory.Count == 0)
            return false;

        var target = _backHistory.Pop();
        NavigateTo(target, pushHistory: false);
        return true;
    }

    [RelayCommand]
    private void GoUp()
    {
        TryGoUp();
    }

    /// <summary>进入父文件夹；没有父目录时返回 false。</summary>
    public bool TryGoUp()
    {
        var parent = ParentPathOf(_currentPath);
        if (parent is null)
            return false;

        NavigateTo(parent, pushHistory: true);
        return true;
    }

    [RelayCommand]
    private void Refresh()
    {
        var selected = _entries.Where(e => e.IsSelected)
            .Select(e => e.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        LoadDirectory(FileListChangeKind.Navigation);

        foreach (var entry in _entries)
        {
            if (selected.Contains(entry.Path))
                entry.IsSelected = true;
        }

        RaiseCommandStates();
    }

    /// <summary>双击：文件夹进入下一级，文件交给系统默认程序打开。</summary>
    public void Open(FileEntry entry)
    {
        if (entry.IsDirectory)
        {
            NavigateTo(entry.Path, pushHistory: true);
            return;
        }

        if (Drive.IsDemo)
        {
            Log.Warn($"示例盘文件不执行打开：{entry.Path}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"打开文件失败：{entry.Path}", ex);
        }
    }

    private void NavigateTo(string path, bool pushHistory)
    {
        if (string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (pushHistory)
            _backHistory.Push(_currentPath);

        _currentPath = path;
        _collapsedGroups.Clear();
        LoadDirectory(FileListChangeKind.Navigation);
        RaiseNavigationStates();
    }

    private void RaiseNavigationStates()
    {
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(CanGoBack));
        GoUpCommand.NotifyCanExecuteChanged();
        GoBackCommand.NotifyCanExecuteChanged();
    }

    private static string? ParentPathOf(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar);
        var index = trimmed.LastIndexOf(Path.DirectorySeparatorChar);
        if (index <= 0)
            return null;

        var parent = trimmed[..index];
        return parent.EndsWith(':') ? parent + Path.DirectorySeparatorChar : parent;
    }

    private void LoadDirectory(FileListChangeKind kind)
    {
        _loadFailed = false;

        try
        {
            _entries = Drive.IsDemo
                ? [.. DemoDriveCatalog.CreateEntries(_currentPath)]
                : ReadRealDirectory(_currentPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _entries = [];
            _loadFailed = true;
            Log.Error($"读取目录失败：{_currentPath}", ex);
        }

        _selectionAnchor = null;

        OnPropertyChanged(nameof(EmptyHint));
        Rebuild(kind);
        RaiseCommandStates();
    }

    private static List<FileEntry> ReadRealDirectory(string path)
    {
        var result = new List<FileEntry>();

        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            try
            {
                if (info is DirectoryInfo directory)
                    result.Add(new FileEntry(directory.FullName, directory.Name, true, directory.LastWriteTime, 0));
                else if (info is FileInfo file)
                    result.Add(new FileEntry(file.FullName, file.Name, false, file.LastWriteTime, file.Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 个别条目读取失败时跳过，不影响整个目录
            }
        }

        return result;
    }

    // ---------- 选择 ----------

    public void SelectEntry(FileEntry entry, bool ctrl, bool shift)
    {
        if (shift && _selectionAnchor is not null)
        {
            var anchorIndex = _displayEntries.IndexOf(_selectionAnchor);
            var index = _displayEntries.IndexOf(entry);

            if (anchorIndex >= 0 && index >= 0)
            {
                ClearSelection(resetAnchor: false);
                var start = Math.Min(anchorIndex, index);
                var end = Math.Max(anchorIndex, index);

                for (var i = start; i <= end; i++)
                    _displayEntries[i].IsSelected = true;
            }
        }
        else if (ctrl)
        {
            entry.IsSelected = !entry.IsSelected;
            _selectionAnchor = entry;
        }
        else
        {
            ClearSelection(resetAnchor: false);
            entry.IsSelected = true;
            _selectionAnchor = entry;
        }

        RaiseCommandStates();
    }

    public void ClearSelection()
    {
        ClearSelection(resetAnchor: true);
        RaiseCommandStates();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var entry in _entries)
            entry.IsSelected = true;

        RaiseCommandStates();
    }

    private void ClearSelection(bool resetAnchor)
    {
        foreach (var entry in _entries)
            entry.IsSelected = false;

        if (resetAnchor)
            _selectionAnchor = null;
    }

    private List<FileEntry> SelectedEntries => _entries.Where(e => e.IsSelected).ToList();

    // ---------- 文件操作 ----------

    [RelayCommand(CanExecute = nameof(CanModify))]
    private void NewFolder()
    {
        try
        {
            var path = UniqueDestination(_currentPath, "新建文件夹", isDirectory: true);
            Directory.CreateDirectory(path);

            LoadDirectory(FileListChangeKind.Navigation);

            var created = _entries.FirstOrDefault(e =>
                string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            if (created is not null)
                SelectEntry(created, ctrl: false, shift: false);
        }
        catch (Exception ex)
        {
            Log.Error($"新建文件夹失败：{_currentPath}", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyOrCut))]
    private void Copy()
    {
        SetClipboard(SelectedEntries.Select(e => e.Path).ToList(), cut: false);
    }

    [RelayCommand(CanExecute = nameof(CanCopyOrCut))]
    private void Cut()
    {
        SetClipboard(SelectedEntries.Select(e => e.Path).ToList(), cut: true);
    }

    private void SetClipboard(List<string> paths, bool cut)
    {
        _clipboardPaths.Clear();
        _clipboardPaths.AddRange(paths);
        _clipboardIsCut = cut;

        // 同步写入系统剪贴板，让资源管理器也能直接粘贴
        FileClipboard.TrySetFiles(paths, cut);

        RaiseCommandStates();
    }

    /// <summary>
    /// 粘贴：优先使用系统剪贴板（可与资源管理器互通），其次用应用内剪贴板。
    /// 复制/移动在后台线程执行并通过进度浮层反馈，避免卡住界面。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private async Task PasteAsync()
    {
        var sources = ResolveClipboard(out var isCut);
        if (sources.Count == 0)
            return;

        await RunTransferAsync(sources, _currentPath, isCut, clearClipboardWhenDone: true);
    }

    /// <summary>拖放进入窗口：把外部/内部文件复制或移动到目标目录。</summary>
    public Task DropTransferAsync(IReadOnlyList<string> sources, string targetDirectory, bool move) =>
        RunTransferAsync(sources, targetDirectory, move, clearClipboardWhenDone: false);

    /// <summary>拖动某一行时参与拖拽的路径：已选中则拖动整个选择，否则只拖该行。</summary>
    public IReadOnlyList<string> GetDragPaths(FileEntry entry) =>
        entry.IsSelected
            ? SelectedEntries.Select(e => e.Path).ToList()
            : [entry.Path];

    public bool IsDemoDrive => Drive.IsDemo;

    public bool CanAcceptDrop => IsRealDrive && !IsOperationInProgress;

    private async Task<bool> RunTransferAsync(IReadOnlyList<string> sources, string targetDirectory,
        bool isCut, bool clearClipboardWhenDone)
    {
        var plans = new List<PastePlan>();

        foreach (var source in sources)
        {
            var isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source))
                continue;

            // 不能把文件夹复制/移动到它自己或它的子目录里
            if (isDirectory && IsSameOrChild(targetDirectory, source))
                continue;

            // 剪切到原目录等同于不操作（与资源管理器一致）
            if (isCut && string.Equals(
                    Path.GetDirectoryName(source.TrimEnd(Path.DirectorySeparatorChar)),
                    targetDirectory.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                continue;

            plans.Add(new PastePlan(source, isDirectory));
        }

        if (plans.Count == 0)
        {
            Log.Info($"拖放：没有可复制的项目（源 {sources.Count} 项，目标={targetDirectory}）");
            return false;
        }

        _operationCts = new CancellationTokenSource();
        _operationGate = new ManualResetEventSlim(true);
        var token = _operationCts.Token;
        var cancelled = false;

        Log.Info($"开始{(isCut ? "移动" : "复制")} {plans.Count} 项 → {targetDirectory}");

        BeginOperation(isCut ? "正在移动" : "正在复制", "正在准备…", canCancel: true, canPause: true);

        var progress = new Progress<FileOperationProgress>(value =>
        {
            IsOperationIndeterminate = false;
            OperationPercent = value.Percent;
            OperationDetail = value.Detail;
            OperationItemsText = $"{value.DoneItems} / {value.TotalItems} 个项目";
            OperationSizeText = $"{FormatBytes(value.DoneBytes)} / {FormatBytes(value.TotalBytes)}";
            OperationSpeedText = IsOperationPaused
                ? "已暂停"
                : value.BytesPerSecond > 0
                    ? $"{FormatBytes((long)value.BytesPerSecond)}/s"
                    : string.Empty;
        });

        try
        {
            var result = await Task.Run(() => ExecutePaste(plans, targetDirectory, isCut, progress, token), token);
            Log.Info($"{(isCut ? "移动" : "复制")}完成：成功 {result.Succeeded} 项，失败 {result.Failed} 项 → {targetDirectory}");
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            Log.Error("文件传输失败", ex);
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            _operationGate?.Dispose();
            _operationGate = null;
            IsOperationPaused = false;
            EndOperation();
        }

        if (isCut && !cancelled && clearClipboardWhenDone)
        {
            _clipboardPaths.Clear();
            _clipboardIsCut = false;
            FileClipboard.Clear();
        }

        LoadDirectory(FileListChangeKind.Navigation);
        return !cancelled;
    }

    private List<string> ResolveClipboard(out bool cut)
    {
        var system = FileClipboard.GetFiles(out var systemCut);
        if (system.Count > 0)
        {
            cut = systemCut;
            return system;
        }

        cut = _clipboardIsCut;
        return [.. _clipboardPaths];
    }

    [RelayCommand]
    private void CancelOperation() => _operationCts?.Cancel();

    [RelayCommand]
    private void TogglePauseOperation()
    {
        if (_operationGate is null)
            return;

        if (IsOperationPaused)
        {
            IsOperationPaused = false;
            _operationGate.Set();
            return;
        }

        IsOperationPaused = true;
        OperationSpeedText = "已暂停";
        _operationGate.Reset();
    }

    /// <summary>后台写入循环在每个分块/每个条目前调用；暂停时阻塞，取消时抛出。</summary>
    private void WaitIfPaused(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _operationGate?.Wait(token);
        token.ThrowIfCancellationRequested();
    }

    private void BeginOperation(string title, string detail, bool canCancel, bool canPause)
    {
        OperationTitle = title;
        OperationDetail = detail;
        OperationItemsText = string.Empty;
        OperationSizeText = string.Empty;
        OperationSpeedText = string.Empty;
        OperationPercent = 0;
        IsOperationIndeterminate = true;
        IsOperationPaused = false;
        CanCancelOperation = canCancel;
        CanPauseOperation = canPause;
        IsOperationInProgress = true;
    }

    private void EndOperation() => IsOperationInProgress = false;

    [RelayCommand(CanExecute = nameof(CanCopyOrCut))]
    private void Delete()
    {
        var selected = SelectedEntries;
        if (selected.Count == 0)
            return;

        DeletePromptText = selected.Count == 1
            ? $"确定要删除“{selected[0].Name}”吗？"
            : $"确定要删除选中的 {selected.Count} 个项目吗？";

        IsDeletePromptVisible = true;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        var selected = SelectedEntries;
        IsDeletePromptVisible = false;

        if (selected.Count == 0)
            return;

        var paths = selected.Select(e => e.Path).ToList();

        BeginOperation("正在删除",
            selected.Count == 1 ? selected[0].Name : $"已选择 {selected.Count} 个项目",
            canCancel: false, canPause: false);

        try
        {
            // SHFileOperation 要求 STA，放到独立的 STA 线程执行
            await Task.Run(() => RunSta(() => Recycle(paths)));
        }
        catch (Exception ex)
        {
            Log.Error("删除到回收站失败", ex);
        }
        finally
        {
            EndOperation();
        }

        LoadDirectory(FileListChangeKind.Navigation);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
            throw error;
    }

    // ---------- 粘贴执行（后台线程） ----------

    private sealed record PastePlan(string Source, bool IsDirectory);

    private sealed class OperationReporter
    {
        private readonly IProgress<FileOperationProgress> _progress;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private TimeSpan _lastReport = TimeSpan.Zero;
        private TimeSpan _lastSpeedTick = TimeSpan.Zero;
        private long _lastSpeedBytes;
        private double _bytesPerSecond;

        public OperationReporter(IProgress<FileOperationProgress> progress) => _progress = progress;

        public void Report(string detail, long doneBytes, long totalBytes, int doneItems, int totalItems,
            bool force = false)
        {
            var now = _watch.Elapsed;

            // 约每 200ms 采样一次瞬时速度，再做平滑，避免数字剧烈跳动
            var speedWindow = now - _lastSpeedTick;
            if (speedWindow.TotalMilliseconds >= 200)
            {
                var instant = (doneBytes - _lastSpeedBytes) / speedWindow.TotalSeconds;
                _bytesPerSecond = _bytesPerSecond <= 0 ? instant : _bytesPerSecond * 0.6 + instant * 0.4;
                _lastSpeedBytes = doneBytes;
                _lastSpeedTick = now;
            }

            // 限制刷新频率，避免海量小文件把 UI 线程淹没
            if (!force && now - _lastReport < TimeSpan.FromMilliseconds(80))
                return;

            _lastReport = now;
            var percent = totalBytes > 0 ? Math.Clamp(doneBytes * 100.0 / totalBytes, 0, 100) : 0;
            _progress.Report(new FileOperationProgress(percent, detail, doneItems, totalItems,
                doneBytes, totalBytes, _bytesPerSecond));
        }
    }

    private (int Succeeded, int Failed) ExecutePaste(IReadOnlyList<PastePlan> plans, string targetDirectory, bool isCut,
        IProgress<FileOperationProgress> progress, CancellationToken token)
    {
        // 先统计总大小与文件数，用于进度百分比
        var sizes = new long[plans.Count];
        var items = new int[plans.Count];
        long totalBytes = 0;
        var totalItems = 0;

        for (var i = 0; i < plans.Count; i++)
        {
            WaitIfPaused(token);
            var plan = plans[i];

            if (plan.IsDirectory)
            {
                CountDirectory(plan.Source, ref sizes[i], ref items[i], token);
            }
            else
            {
                items[i] = 1;
                try { sizes[i] = new FileInfo(plan.Source).Length; } catch { sizes[i] = 0; }
            }

            totalBytes += sizes[i];
            totalItems += items[i];
        }

        var reporter = new OperationReporter(progress);
        long doneBytes = 0;
        var doneItems = 0;
        var succeeded = 0;
        var failed = 0;
        reporter.Report("准备完成", 0, totalBytes, 0, totalItems, force: true);

        for (var i = 0; i < plans.Count; i++)
        {
            WaitIfPaused(token);
            var plan = plans[i];
            var name = Path.GetFileName(plan.Source.TrimEnd(Path.DirectorySeparatorChar));

            try
            {
                var destination = UniqueDestination(targetDirectory, name, plan.IsDirectory);
                var deleteSource = isCut;

                if (isCut)
                {
                    try
                    {
                        if (plan.IsDirectory)
                            Directory.Move(plan.Source, destination);
                        else
                            File.Move(plan.Source, destination);

                        doneBytes += sizes[i];
                        doneItems += items[i];
                        succeeded++;
                        reporter.Report($"正在移动 {name}", doneBytes, totalBytes, doneItems, totalItems,
                            force: true);
                        continue;
                    }
                    catch (IOException) when (!token.IsCancellationRequested)
                    {
                        // 跨卷等情况退化为复制后删除
                        deleteSource = true;
                    }
                }

                if (plan.IsDirectory)
                {
                    CopyDirectoryWithProgress(plan.Source, destination, ref doneBytes, ref doneItems,
                        totalBytes, totalItems, reporter, token);
                }
                else
                {
                    CopyFileWithProgress(plan.Source, destination, ref doneBytes, ref doneItems,
                        totalBytes, totalItems, reporter, token);
                }

                if (deleteSource)
                {
                    if (plan.IsDirectory)
                        Directory.Delete(plan.Source, recursive: true);
                    else
                        File.Delete(plan.Source);
                }

                succeeded++;
                Log.Info($"已{(isCut ? "移动" : "复制")}：{plan.Source} → {destination}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                Log.Error($"粘贴失败：{plan.Source}", ex);
            }
        }

        return (succeeded, failed);
    }

    private void CopyFileWithProgress(string source, string destination,
        ref long doneBytes, ref int doneItems, long totalBytes, int totalItems,
        OperationReporter reporter, CancellationToken token)
    {
        WaitIfPaused(token);
        var buffer = new byte[1024 * 1024];

        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                buffer.Length, FileOptions.SequentialScan);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, buffer.Length);

            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                WaitIfPaused(token);
                output.Write(buffer, 0, read);
                doneBytes += read;
                reporter.Report($"正在复制 {Path.GetFileName(source)}", doneBytes, totalBytes,
                    doneItems, totalItems);
            }
        }
        catch
        {
            // 取消或失败时清理未完成的副本
            try
            {
                if (File.Exists(destination))
                    File.Delete(destination);
            }
            catch
            {
                // 忽略清理失败
            }

            throw;
        }

        try { File.SetLastWriteTime(destination, File.GetLastWriteTime(source)); } catch { }

        doneItems++;
        reporter.Report($"正在复制 {Path.GetFileName(source)}", doneBytes, totalBytes, doneItems,
            totalItems, force: true);
    }

    private void CopyDirectoryWithProgress(string source, string destination,
        ref long doneBytes, ref int doneItems, long totalBytes, int totalItems,
        OperationReporter reporter, CancellationToken token)
    {
        WaitIfPaused(token);
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            CopyFileWithProgress(file, Path.Combine(destination, Path.GetFileName(file)),
                ref doneBytes, ref doneItems, totalBytes, totalItems, reporter, token);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectoryWithProgress(directory, Path.Combine(destination, Path.GetFileName(directory)),
                ref doneBytes, ref doneItems, totalBytes, totalItems, reporter, token);
        }

        try { Directory.SetLastWriteTime(destination, Directory.GetLastWriteTime(source)); } catch { }
    }

    private void CountDirectory(string path, ref long bytes, ref int items, CancellationToken token)
    {
        WaitIfPaused(token);

        foreach (var file in Directory.EnumerateFiles(path))
        {
            WaitIfPaused(token);
            try { bytes += new FileInfo(file).Length; } catch { }
            items++;
        }

        foreach (var directory in Directory.EnumerateDirectories(path))
            CountDirectory(directory, ref bytes, ref items, token);
    }

    [RelayCommand]
    private void CancelDelete() => IsDeletePromptVisible = false;

    [RelayCommand(CanExecute = nameof(CanRename))]
    private void Rename()
    {
        var entry = SelectedEntries.FirstOrDefault();
        if (entry is null)
            return;

        _renameEntry = entry;
        RenameText = entry.Name;
        IsRenamePromptVisible = true;
    }

    [RelayCommand]
    private void ConfirmRename()
    {
        var entry = _renameEntry;
        var newName = RenameText.Trim();
        IsRenamePromptVisible = false;

        if (entry is null || newName.Length == 0 || string.Equals(newName, entry.Name, StringComparison.Ordinal))
            return;

        try
        {
            var destination = Path.Combine(_currentPath, newName);
            if (entry.IsDirectory)
                Directory.Move(entry.Path, destination);
            else
                File.Move(entry.Path, destination);

            LoadDirectory(FileListChangeKind.Navigation);
        }
        catch (Exception ex)
        {
            Log.Error($"重命名失败：{entry.Path}", ex);
        }
    }

    [RelayCommand]
    private void CancelRename() => IsRenamePromptVisible = false;

    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    private void OpenSelected()
    {
        var entry = SelectedEntries.FirstOrDefault();
        if (entry is not null)
            Open(entry);
    }

    private bool CanOpenSelected() => _entries.Any(e => e.IsSelected);

    /// <summary>当前是否有选中项（供右键菜单等界面使用）。</summary>
    public bool HasSelection => _entries.Any(e => e.IsSelected);

    private bool CanModify() => IsRealDrive && !IsOperationInProgress;

    private bool CanCopyOrCut() =>
        IsRealDrive && !IsOperationInProgress && _entries.Any(e => e.IsSelected);

    private bool CanPaste() => IsRealDrive && !IsOperationInProgress;

    private bool CanRename() =>
        IsRealDrive && !IsOperationInProgress && _entries.Count(e => e.IsSelected) == 1;

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(HasSelection));

        CopyCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        OpenSelectedCommand.NotifyCanExecuteChanged();
    }

    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    private static string FormatBytes(long bytes)
    {
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < SizeUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {SizeUnits[unit]}";
    }

    private static bool IsSameOrChild(string candidate, string parent)
    {
        var normalizedParent = parent.TrimEnd(Path.DirectorySeparatorChar);
        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar);

        return string.Equals(normalizedCandidate, normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>目标目录中生成不冲突的名称：name、name (2)、name (3)……</summary>
    private static string UniqueDestination(string directory, string name, bool isDirectory)
    {
        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isDirectory ? string.Empty : Path.GetExtension(name);

        var candidate = Path.Combine(directory, name);
        var index = 1;

        while (File.Exists(candidate) || Directory.Exists(candidate))
            candidate = Path.Combine(directory, $"{stem} ({++index}){extension}");

        return candidate;
    }

    // ---------- 删除到回收站 ----------

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);

    private static void Recycle(IEnumerable<string> paths)
    {
        // 路径列表要求双 null 结尾
        var from = string.Join('\0', paths) + "\0\0";
        var operation = new ShFileOpStruct
        {
            wFunc = FO_DELETE,
            pFrom = from,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };

        var result = SHFileOperation(ref operation);
        if (result != 0)
            throw new IOException($"删除到回收站失败（错误码 0x{result:X}）");
    }

    // ---------- 排序 / 分组 ----------

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
        _displayEntries.Clear();

        if (GroupBy == FileGroupBy.None)
        {
            foreach (var entry in SortedEntries(_entries))
            {
                Items.Add(entry);
                _displayEntries.Add(entry);
            }
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
                {
                    group.Entries.Add(entry);
                    _displayEntries.Add(entry);
                }

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

    partial void OnIsOperationInProgressChanged(bool value) => RaiseCommandStates();

    partial void OnOperationPercentChanged(double value) => OnPropertyChanged(nameof(OperationPercentText));

    partial void OnIsOperationPausedChanged(bool value) => OnPropertyChanged(nameof(OperationPauseText));

    partial void OnIsOperationIndeterminateChanged(bool value) =>
        OnPropertyChanged(nameof(OperationPercentText));

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
