using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentUSB.Models;
using FluentUSB.Services;
using FluentUSB.ViewModels;

namespace FluentUSB.Views;

/// <summary>内建文件浏览器窗口（M2）：列出 U 盘内容并支持按列排序。</summary>
public partial class FileExplorerWindow : Window, IAppearanceHost
{
    private enum ResizeTarget
    {
        None,
        Name,
        Modified,
        Type,
        Size,
    }

    // 菜单浮层展开：从上方 6px 处滑入并淡入
    private static readonly TransformOperations MenuHiddenTransform =
        TransformOperations.Parse("translateY(-6px)");
    private static readonly TransformOperations MenuShownTransform =
        TransformOperations.Parse("translateY(0px)");

    // 列表无专属动画时的常规入场：从下方 8px 滑入
    private static readonly TransformOperations RowHiddenTransform =
        TransformOperations.Parse("translateY(8px)");
    // 升降序切换：从与当前顺序相反的方向滑入，强化"反转"的观感
    private static readonly TransformOperations RowHiddenFromTopTransform =
        TransformOperations.Parse("translateY(-28px)");
    private static readonly TransformOperations RowHiddenFromBottomTransform =
        TransformOperations.Parse("translateY(28px)");
    private static readonly TransformOperations RowShownTransform =
        TransformOperations.Parse("translateY(0px)");
    // 切换文件夹：旧文件向左甩出，新文件从右侧甩入；返回父级时方向相反
    private static readonly TransformOperations RowExitLeftTransform =
        TransformOperations.Parse("translateX(-220px)");
    private static readonly TransformOperations RowHiddenFromRightTransform =
        TransformOperations.Parse("translateX(220px)");
    private static readonly TransformOperations RowExitRightTransform =
        TransformOperations.Parse("translateX(220px)");
    private static readonly TransformOperations RowHiddenFromLeftTransform =
        TransformOperations.Parse("translateX(-220px)");

    private bool _isNavigating;
    private bool _suppressListAnimation;

    // 拖放过程中的文件路径缓存（一次拖放只解析一次）
    private List<string>? _dragFilePaths;

    // 「分组依据」二级菜单展开后的高度：5 行 × 34 + 上下留白
    private const double GroupByMenuHeight = 174;

    private readonly FileExplorerViewModel _viewModel;
    private DispatcherTimer? _rowTimer;

    // 触屏长按弹出右键菜单
    private DispatcherTimer? _holdTimer;
    private Border? _holdRow;
    private Point _holdStart;

    // 鼠标拖拽：按住行后移动超过阈值才开始拖出
    private PointerPressedEventArgs? _dragPressed;
    private FileEntry? _dragEntry;
    private Point _dragStartPoint;
    private Border? _dropRow;

    private ResizeTarget _resizeTarget;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private IPointer? _resizePointer;

    /// <summary>仅供设计器预览使用；运行时请使用带 drive 参数的构造函数。</summary>
    public FileExplorerWindow()
        : this(DemoDriveCatalog.Drive)
    {
    }

    public FileExplorerWindow(UsbDriveInfo drive)
    {
        InitializeComponent();

        _viewModel = new FileExplorerViewModel(drive);
        DataContext = _viewModel;
        Title = $"{drive.DisplayName} - FluentUSB";

        // 浮层锚定到菜单栏按钮（放置目标必须显式指定）
        SortPopup.PlacementTarget = SortMenuButton;
        ViewPopup.PlacementTarget = ViewMenuButton;
        RowMenuPopup.PlacementTarget = this;

        // 通过点击空白处收起时，同步复位按钮的激活态
        SortPopup.Closed += (_, _) => SortMenuButton.Classes.Set("open", false);
        ViewPopup.Closed += (_, _) => ViewMenuButton.Classes.Set("open", false);

        _viewModel.Rebuilt += OnListRebuilt;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 触屏滚动时取消长按，避免滚动过程中误弹菜单
        AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => CancelHold());

        // 拖入文件：走 Avalonia 的托管拖放通道。
        // DragDrop.AllowDrop 是「继承型」附加属性，在窗口上开启后整棵可视树都会成为拖放目标；
        // 不开启的话 DragDropDevice 找不到目标，资源管理器里的光标会一直是「打叉」。
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // 窗口显示后才有句柄，此时才能应用原生背景效果
        Opened += (_, _) => BackdropService.Apply(this);
        ActualThemeVariantChanged += (_, _) => BackdropService.Apply(this);
        Closed += (_, _) =>
        {
            _rowTimer?.Stop();
            CancelHold();
            ClearDropHighlight();
            _viewModel.Rebuilt -= OnListRebuilt;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            SortPopup.IsOpen = false;
            ViewPopup.IsOpen = false;
            RowMenuPopup.IsOpen = false;
        };
    }

    public Border? SurfaceHost => Surface;

    // ---------- 文件行交互 ----------

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: FileEntry entry } row)
            return;

        var point = e.GetCurrentPoint(this);

        // 右键：先选中该行，再弹出右键菜单
        if (point.Properties.IsRightButtonPressed)
        {
            if (!entry.IsSelected)
                _viewModel.SelectEntry(entry, ctrl: false, shift: false);

            RowMenuPopup.IsOpen = true;
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        _viewModel.SelectEntry(
            entry,
            ctrl: e.KeyModifiers.HasFlag(KeyModifiers.Control),
            shift: e.KeyModifiers.HasFlag(KeyModifiers.Shift));

        // 触屏长按弹出同一份菜单
        if (e.Pointer.Type == PointerType.Touch)
        {
            _holdStart = e.GetPosition(this);
            StartHold(row);
            return;
        }

        // 鼠标：记录起点并捕获指针，移动超过阈值后开始拖拽
        _dragPressed = e;
        _dragEntry = entry;
        _dragStartPoint = e.GetPosition(this);
        e.Pointer.Capture(row);

        e.Handled = true;
    }

    private async void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPressed is not null)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _dragPressed = null;
                _dragEntry = null;
            }
            else
            {
                var current = e.GetPosition(this);
                if (Math.Abs(current.X - _dragStartPoint.X) > 6
                    || Math.Abs(current.Y - _dragStartPoint.Y) > 6)
                    await BeginDragAsync();
            }
        }

        if (_holdRow is null)
            return;

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _holdStart.X) > 8 || Math.Abs(position.Y - _holdStart.Y) > 8)
            CancelHold();
    }

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragPressed = null;
        _dragEntry = null;
        e.Pointer.Capture(null);
        CancelHold();
    }

    private void OnRowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _dragPressed = null;
        _dragEntry = null;
        CancelHold();
    }

    // ---------- 拖出到资源管理器 / 拖入窗口 ----------

    private async Task BeginDragAsync()
    {
        var pressed = _dragPressed;
        var entry = _dragEntry;
        _dragPressed = null;
        _dragEntry = null;

        if (pressed is null || entry is null || _viewModel.IsDemoDrive)
            return;

        // 只允许鼠标拖拽，避免与触屏滚动、长按冲突
        if (pressed.Pointer.Type != PointerType.Mouse)
            return;

        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null)
            return;

        if (!entry.IsSelected)
            _viewModel.SelectEntry(entry, ctrl: false, shift: false);

        var data = new DataTransfer();

        foreach (var path in _viewModel.GetDragPaths(entry))
        {
            try
            {
                var isDirectory = Directory.Exists(path);
                IStorageItem? storage = isDirectory
                    ? await provider.TryGetFolderFromPathAsync(path)
                    : await provider.TryGetFileFromPathAsync(path);

                if (storage is not null)
                    data.Add(DataTransferItem.CreateFile(storage));
            }
            catch
            {
                // 单个条目无法转换时跳过
            }
        }

        if (data.Items.Count == 0)
            return;

        // OLE 拖拽接管指针前释放本地捕获
        pressed.Pointer.Capture(null);

        try
        {
            await DragDrop.DoDragDropAsync(pressed, data,
                DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch
        {
            // 拖拽被取消时忽略
        }
    }

    // ---------- 托管拖放（资源管理器 → 本窗口） ----------

    /// <summary>拖放悬停进入：决定允许的效果并准备文件列表。</summary>
    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        _dragFilePaths = null;
        var paths = GetDragFilePaths(e);

        Log.Info($"拖放进入：文件 {paths.Count} 个，可写入={_viewModel.CanAcceptDrop}");
        e.DragEffects = ResolveDropEffects(paths);
    }

    /// <summary>拖放悬停移动：更新落点高亮。</summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var paths = GetDragFilePaths(e);
        e.DragEffects = ResolveDropEffects(paths);

        if (e.DragEffects == DragDropEffects.None)
        {
            ClearDropHighlight();
            return;
        }

        var (directory, row) = ResolveDropTargetFromClient(e.GetPosition(this));
        SetDropHighlightRow(row);
    }

    /// <summary>拖放离开：清理高亮与缓存。</summary>
    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _dragFilePaths = null;
        ClearDropHighlight();
    }

    /// <summary>放下文件：复制（或移动）到落点目录。</summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = GetDragFilePaths(e);
        _dragFilePaths = null;
        ClearDropHighlight();

        if (!_viewModel.CanAcceptDrop)
        {
            Log.Info("拖放被忽略：当前不可写入（示例盘或正在执行操作）");
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (paths.Count == 0)
        {
            Log.Info("拖放被忽略：未读到文件路径");
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var (directory, _) = ResolveDropTargetFromClient(e.GetPosition(this));
        directory ??= _viewModel.PathText;

        // 资源管理器里按住 Shift 表示移动，默认复制
        var move = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        e.DragEffects = move ? DragDropEffects.Move : DragDropEffects.Copy;

        Log.Info($"拖放落点：目标={directory}，文件数={paths.Count}，移动={move}");
        Log.Info($"拖放文件：{string.Join("、", paths.Take(10))}{(paths.Count > 10 ? " …" : string.Empty)}");

        _ = SafeDropAsync(paths, directory, move);
    }

    /// <summary>当前拖放允许的效果：不可写入或没有文件时一律拒绝。</summary>
    private DragDropEffects ResolveDropEffects(List<string> paths) =>
        paths.Count > 0 && _viewModel.CanAcceptDrop
            ? DragDropEffects.Copy | DragDropEffects.Move
            : DragDropEffects.None;

    /// <summary>取出拖放中的本地文件路径（一次拖放只解析一次）。</summary>
    private List<string> GetDragFilePaths(DragEventArgs e)
    {
        if (_dragFilePaths is not null)
            return _dragFilePaths;

        var paths = new List<string>();

        try
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files is not null)
            {
                foreach (var file in files)
                {
                    var path = file.TryGetLocalPath();
                    if (!string.IsNullOrEmpty(path))
                        paths.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取拖放文件失败", ex);
        }

        // 只缓存成功解析到的结果：空结果留待下次事件重试，
        // 避免一次瞬时失败让整次拖放都被判定为「不可放入」
        if (paths.Count > 0)
            _dragFilePaths = paths;

        return paths;
    }

    private async Task SafeDropAsync(List<string> paths, string directory, bool move)
    {
        try
        {
            await _viewModel.DropTransferAsync(paths, directory, move);
        }
        catch (Exception ex)
        {
            Log.Error("拖放复制失败", ex);
        }
    }

    /// <summary>
    /// 拖放目标：按窗口客户区坐标命中文件夹行则进入该文件夹，否则落在当前目录。
    /// 指针不在文件列表区域时返回 (null, null)（由调用方决定是否落到当前目录）。
    /// </summary>
    private (string? Directory, Border? Row) ResolveDropTargetFromClient(Point clientInWindow)
    {
        var position = this.TranslatePoint(clientInWindow, FileList);
        if (!position.HasValue)
            return (_viewModel.PathText, null);

        var p = position.Value;
        if (p.X < 0 || p.Y < 0
            || p.X > FileList.Bounds.Width || p.Y > FileList.Bounds.Height)
            return (null, null);

        if (FileList.InputHitTest(p) is Visual hit)
        {
            foreach (var ancestor in hit.GetSelfAndVisualAncestors())
            {
                if (ancestor is Border border
                    && border.Classes.Contains("FileRow")
                    && border.DataContext is FileEntry { IsDirectory: true } folder)
                    return (folder.Path, border);
            }
        }

        return (_viewModel.PathText, null);
    }

    private void SetDropHighlightRow(Border? row)
    {
        if (ReferenceEquals(_dropRow, row))
            return;

        _dropRow?.Classes.Set("dropTarget", false);
        _dropRow = row;
        _dropRow?.Classes.Set("dropTarget", true);
    }

    private void ClearDropHighlight()
    {
        _dropRow?.Classes.Set("dropTarget", false);
        _dropRow = null;
    }

    private void StartHold(Border row)
    {
        CancelHold();
        _holdRow = row;

        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _holdTimer.Tick += (_, _) =>
        {
            CancelHold();

            if (row.DataContext is not FileEntry entry)
                return;

            if (!entry.IsSelected)
                _viewModel.SelectEntry(entry, ctrl: false, shift: false);

            RowMenuPopup.IsOpen = true;
        };

        _holdTimer.Start();
    }

    private void CancelHold()
    {
        _holdTimer?.Stop();
        _holdTimer = null;
        _holdRow = null;
    }

    private async void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border { DataContext: FileEntry entry })
            return;

        e.Handled = true;

        if (entry.IsDirectory)
        {
            await NavigateAnimatedAsync(reverse: false, () => _viewModel.Open(entry));
            return;
        }

        _viewModel.Open(entry);
    }

    // 上一级 / 后退：方向与进入子文件夹相反
    private async void OnGoUpClick(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanGoUp)
            return;

        await NavigateAnimatedAsync(reverse: true, () => _viewModel.TryGoUp());
    }

    private async void OnGoBackClick(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanGoBack)
            return;

        await NavigateAnimatedAsync(reverse: true, () => _viewModel.TryGoBack());
    }

    /// <summary>
    /// 目录切换动画：当前文件快速甩出，约 40ms 后新内容以反向甩入。
    /// reverse 为 true 时方向相反（返回父级 / 后退）。
    /// </summary>
    private async Task NavigateAnimatedAsync(bool reverse, Action navigate)
    {
        if (_isNavigating)
            return;

        _isNavigating = true;

        try
        {
            var exitTransform = reverse ? RowExitRightTransform : RowExitLeftTransform;
            var enterTransform = reverse ? RowHiddenFromLeftTransform : RowHiddenFromRightTransform;

            // 双击/点击后立刻开始甩出，不等它播完
            StartRowsExit(GetListRows(), exitTransform);

            // 甩出刚开始就把新内容甩进来，衔接更紧凑
            await Task.Delay(40);

            FileList.Opacity = 0;
            _suppressListAnimation = true;
            navigate();

            Dispatcher.UIThread.Post(() =>
            {
                _suppressListAnimation = false;
                FileList.Opacity = 1;
                AnimateRows(GetListRows(), enterTransform, 140, 3, new CubicEaseOut());
            }, DispatcherPriority.Background);
        }
        finally
        {
            _isNavigating = false;
        }
    }

    private void StartRowsExit(IReadOnlyList<Control> rows, TransformOperations exitTransform)
    {
        if (rows.Count == 0)
            return;

        const int staggerMs = 2;
        const int durationMs = 100;

        try
        {
            _rowTimer?.Stop();

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var delay = Math.Min(i, 16) * staggerMs;

                DispatcherTimer.RunOnce(() =>
                {
                    // 甩出：越走越快（加速）
                    row.Transitions = CreateRowTransitions(durationMs, new CubicEaseIn());
                    row.Opacity = 0;
                    row.RenderTransform = exitTransform;
                }, TimeSpan.FromMilliseconds(delay));
            }
        }
        catch
        {
            // 动画失败时直接切换
        }
    }

    // 点击列表空白处取消选择；右键空白处弹出菜单（点在滚动条上时忽略）
    private void OnListBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual
            && visual.GetSelfAndVisualAncestors().OfType<ScrollBar>().Any())
            return;

        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsRightButtonPressed)
        {
            _viewModel.ClearSelection();
            RowMenuPopup.IsOpen = true;
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
            _viewModel.ClearSelection();
    }

    // ---------- 键盘快捷键 ----------

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel.IsRenamePromptVisible)
        {
            if (e.Key == Key.Enter)
            {
                _viewModel.ConfirmRenameCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _viewModel.CancelRenameCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        if (_viewModel.IsDeletePromptVisible)
        {
            if (e.Key == Key.Escape)
            {
                _viewModel.CancelDeleteCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.Delete when _viewModel.DeleteCommand.CanExecute(null):
                _viewModel.DeleteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F2 when _viewModel.RenameCommand.CanExecute(null):
                _viewModel.RenameCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.A when ctrl:
                _viewModel.SelectAllCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.C when ctrl && _viewModel.CopyCommand.CanExecute(null):
                _viewModel.CopyCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.X when ctrl && _viewModel.CutCommand.CanExecute(null):
                _viewModel.CutCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.V when ctrl && _viewModel.PasteCommand.CanExecute(null):
                _viewModel.PasteCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileExplorerViewModel.IsRenamePromptVisible)
            || !_viewModel.IsRenamePromptVisible)
            return;

        // 浮层出现后聚焦输入框并全选文件名
        Dispatcher.UIThread.Post(() =>
        {
            RenameBox.Focus();
            RenameBox.SelectAll();
        }, DispatcherPriority.Background);
    }

    // ---------- 顶部菜单 ----------

    private void OnSortMenuClick(object? sender, RoutedEventArgs e)
    {
        ViewPopup.IsOpen = false;
        SortPopup.IsOpen = !SortPopup.IsOpen;
        SortMenuButton.Classes.Set("open", SortPopup.IsOpen);
    }

    private void OnViewMenuClick(object? sender, RoutedEventArgs e)
    {
        SortPopup.IsOpen = false;
        ViewPopup.IsOpen = !ViewPopup.IsOpen;
        ViewMenuButton.Classes.Set("open", ViewPopup.IsOpen);
    }

    // 点了具体菜单项后收起浮层
    private void OnMenuActionClick(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SortPopup.IsOpen = false;
            ViewPopup.IsOpen = false;
            RowMenuPopup.IsOpen = false;
            SortMenuButton.Classes.Set("open", false);
            ViewMenuButton.Classes.Set("open", false);
        }, DispatcherPriority.Background);
    }

    // 「分组依据」二级菜单：高度与透明度平滑展开/收起
    private void OnGroupByRowClick(object? sender, RoutedEventArgs e)
    {
        var expanded = GroupBySection.Height < 1;
        GroupBySection.Height = expanded ? GroupByMenuHeight : 0;
        GroupBySection.Opacity = expanded ? 1 : 0;
        GroupByChevron.Classes.Set("expanded", expanded);
    }

    private void OnMenuPopupOpened(object? sender, EventArgs e)
    {
        if (sender is Popup { Child: Border card })
            PlayMenuOpenAnimation(card);
    }

    private static void PlayMenuOpenAnimation(Border card)
    {
        try
        {
            card.Transitions = null;
            card.Opacity = 0;
            card.RenderTransform = MenuHiddenTransform;

            Dispatcher.UIThread.Post(() =>
            {
                var easing = new CubicEaseOut();
                card.Transitions = new Transitions
                {
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(140),
                        Easing = easing,
                    },
                    new TransformOperationsTransition
                    {
                        Property = Visual.RenderTransformProperty,
                        Duration = TimeSpan.FromMilliseconds(180),
                        Easing = easing,
                    },
                };

                card.Opacity = 1;
                card.RenderTransform = MenuShownTransform;
            }, DispatcherPriority.Render);
        }
        catch
        {
            card.Opacity = 1;
            card.RenderTransform = MenuShownTransform;
        }
    }

    // ---------- 列表动画 ----------

    private void OnListRebuilt(FileListChangeKind kind)
    {
        // 初始加载不需要入场动画
        if (kind == FileListChangeKind.Initial)
            return;

        // 文件夹切换自带甩入动画，跳过通用入场
        if (_suppressListAnimation)
        {
            _suppressListAnimation = false;
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (kind == FileListChangeKind.SortDirection)
                PlayDirectionAnimation();
            else
                PlayListAnimation();
        }, DispatcherPriority.Background);
    }

    // 升降序切换：从当前方向的反方向滑入，直观体现"反序"
    private void PlayDirectionAnimation()
    {
        var hidden = _viewModel.SortDescending
            ? RowHiddenFromTopTransform
            : RowHiddenFromBottomTransform;

        AnimateRows(GetListRows(), hidden, 220, 10);
    }

    private void PlayListAnimation() =>
        AnimateRows(GetListRows(), RowHiddenTransform, 180, 12);

    private List<Control> GetListRows() =>
        FileList.GetVisualDescendants()
            .OfType<Control>()
            .Where(IsAnimatedRow)
            .ToList();

    /// <summary>让指定行走同一套淡入 + 位移动画，按行错峰。</summary>
    private void AnimateRows(IReadOnlyList<Control> rows, TransformOperations hidden,
        int durationMs, int staggerMs, Easing? easing = null)
    {
        try
        {
            if (rows.Count == 0)
                return;

            _rowTimer?.Stop();

            foreach (var row in rows)
            {
                row.Transitions = null;
                row.Opacity = 0;
                row.RenderTransform = hidden;
            }

            var index = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Send)
            {
                Interval = TimeSpan.FromMilliseconds(staggerMs),
            };

            timer.Tick += (_, _) =>
            {
                if (index >= rows.Count)
                {
                    timer.Stop();
                    return;
                }

                var row = rows[index++];
                row.Transitions = CreateRowTransitions(durationMs, easing);
                row.Opacity = 1;
                row.RenderTransform = RowShownTransform;
            };

            _rowTimer = timer;
            timer.Start();
        }
        catch
        {
            // 动画失败时直接显示，不影响功能
            foreach (var row in rows)
            {
                row.Opacity = 1;
                row.RenderTransform = RowShownTransform;
            }
        }
    }

    private static bool IsAnimatedRow(Control control) =>
        (control is Border border && border.Classes.Contains("FileRow"))
        || (control is Button button && button.Classes.Contains("GroupHeader"));

    private static Transitions CreateRowTransitions(int durationMs, Easing? easing = null)
    {
        easing ??= new CubicEaseOut();
        return new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                Easing = easing,
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                Easing = easing,
            },
        };
    }

    // ---------- 分组开合 ----------

    private void OnGroupHeaderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FileGroupRow row })
            return;

        // 只切换该分组状态；内容区的高度/透明度过渡负责动画，不重建列表
        _viewModel.ToggleGroup(row.Key);
    }

    // ---------- 表头列宽拖拽 ----------

    // 按住表头列边界开始拖拽（列宽由 ViewModel 提供，表头与文件行同步变化）
    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control grip || DataContext is not FileExplorerViewModel)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _resizeTarget = (grip.Tag as string) switch
        {
            "Name" => ResizeTarget.Name,
            "Modified" => ResizeTarget.Modified,
            "Type" => ResizeTarget.Type,
            "Size" => ResizeTarget.Size,
            _ => ResizeTarget.None,
        };

        if (_resizeTarget == ResizeTarget.None)
            return;

        _resizeStartX = e.GetPosition(this).X;
        _resizeStartWidth = HeaderGrid.ColumnDefinitions[ColumnIndex(_resizeTarget)].ActualWidth;
        _resizePointer = e.Pointer;

        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (_resizeTarget == ResizeTarget.None || DataContext is not FileExplorerViewModel vm)
            return;

        var width = Math.Max(MinColumnWidth(_resizeTarget),
            _resizeStartWidth + e.GetPosition(this).X - _resizeStartX);

        switch (_resizeTarget)
        {
            case ResizeTarget.Name:
                vm.NameColumnWidth = new GridLength(width);
                break;
            case ResizeTarget.Modified:
                vm.ModifiedColumnWidth = new GridLength(width);
                break;
            case ResizeTarget.Type:
                vm.TypeColumnWidth = new GridLength(width);
                break;
            case ResizeTarget.Size:
                vm.SizeColumnWidth = new GridLength(width);
                break;
        }

        e.Handled = true;
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndColumnResize();
        e.Handled = true;
    }

    private void OnGripCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndColumnResize();

    private void EndColumnResize()
    {
        _resizePointer?.Capture(null);
        _resizePointer = null;
        _resizeTarget = ResizeTarget.None;
    }

    private static int ColumnIndex(ResizeTarget target) => target switch
    {
        ResizeTarget.Name => 0,
        ResizeTarget.Modified => 1,
        ResizeTarget.Type => 2,
        _ => 3,
    };

    // 每列的最小宽度，避免拖到看不见列名
    private static double MinColumnWidth(ResizeTarget target) => target switch
    {
        ResizeTarget.Name => 120,
        ResizeTarget.Modified => 90,
        ResizeTarget.Type => 80,
        _ => 70,
    };
}
