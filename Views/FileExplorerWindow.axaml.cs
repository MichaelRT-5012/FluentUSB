using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
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

    // 「分组依据」二级菜单展开后的高度：5 行 × 34 + 上下留白
    private const double GroupByMenuHeight = 174;

    private readonly FileExplorerViewModel _viewModel;
    private DispatcherTimer? _rowTimer;

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

        // 通过点击空白处收起时，同步复位按钮的激活态
        SortPopup.Closed += (_, _) => SortMenuButton.Classes.Set("open", false);
        ViewPopup.Closed += (_, _) => ViewMenuButton.Classes.Set("open", false);

        _viewModel.Rebuilt += OnListRebuilt;

        // 窗口显示后才有句柄，此时才能应用原生背景效果
        Opened += (_, _) => BackdropService.Apply(this);
        ActualThemeVariantChanged += (_, _) => BackdropService.Apply(this);
        Closed += (_, _) =>
        {
            _rowTimer?.Stop();
            _viewModel.Rebuilt -= OnListRebuilt;
            SortPopup.IsOpen = false;
            ViewPopup.IsOpen = false;
        };
    }

    public Border? SurfaceHost => Surface;

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
        int durationMs, int staggerMs)
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
                row.Transitions = CreateRowTransitions(durationMs);
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

    private static Transitions CreateRowTransitions(int durationMs)
    {
        var easing = new CubicEaseOut();
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
