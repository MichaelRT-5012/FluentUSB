using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentUSB.Models;
using FluentUSB.Services;

namespace FluentUSB.ViewModels;

/// <summary>设置页里的一个背景效果选项。</summary>
public sealed class BackdropOptionViewModel : ObservableObject
{
    private readonly Action<BackdropKind> _select;
    private bool _isSelected;

    public BackdropOptionViewModel(BackdropKind kind, bool supported, Action<BackdropKind> select)
    {
        Kind = kind;
        IsSupported = supported;
        _select = select;

        Title = BackdropCatalog.Title(kind);
        Description = BackdropCatalog.Description(kind);
        Note = supported
            ? string.Empty
            : BackdropCatalog.Requirement(kind);
    }

    public BackdropKind Kind { get; }

    public bool IsSupported { get; }

    public string Title { get; }

    public string Description { get; }

    public string Note { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value) || !value)
                return;

            _select(Kind);
        }
    }
}

/// <summary>
/// 设置页视图模型：所有修改即时生效并持久化。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private bool _synchronizing;

    public SettingsViewModel()
    {
        var settings = SettingsStore.Current;
        var capabilities = BackdropService.Capabilities;

        foreach (var kind in Enum.GetValues<BackdropKind>())
        {
            BackdropOptions.Add(new BackdropOptionViewModel(
                kind,
                capabilities.IsSupported(kind),
                SelectBackdrop));

            ToastBackdropOptions.Add(new BackdropOptionViewModel(
                kind,
                capabilities.IsSupported(kind),
                SelectToastBackdrop));
        }

        var build = capabilities.Build;
        SystemText = $"{(build >= 22000 ? "Windows 11" : "Windows 10")}（内部版本 {build}）";
        SettingsFilePath = SettingsStore.SettingsFilePath;
        SettingsFolderPath = Path.GetDirectoryName(SettingsStore.SettingsFilePath) ?? string.Empty;
        LogFolderPath = Log.DirectoryPath;

        var version = typeof(SettingsViewModel).Assembly.GetName().Version;
        VersionText = version is null
            ? "版本 0.1.0"
            : $"版本 {version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

        // 便于确认当前运行的是哪次构建
        try
        {
            var exe = Environment.ProcessPath;
            BuildText = exe is null
                ? "未知"
                : $"构建于 {File.GetLastWriteTime(exe):yyyy-MM-dd HH:mm}";
        }
        catch (IOException)
        {
            BuildText = "未知";
        }

        _surfaceOpacityPercent = Math.Round(settings.SurfaceOpacity * 100);
        _toastOpacityPercent = Math.Round(settings.ToastOpacity * 100);
        _toastStyleIndex = (int)settings.ToastStyle;
        _themeIndex = (int)settings.Theme;
        _popupPositionIndex = (int)settings.PopupPosition;
        _popupDurationSeconds = settings.PopupDurationSeconds;

        SyncSelection(settings.Backdrop);
        SyncToastSelection(settings.ToastBackdrop);
        UpdateOpacityAvailability();
    }

    public ObservableCollection<BackdropOptionViewModel> BackdropOptions { get; } = [];

    /// <summary>插入弹窗的可选背景效果（仅整窗式生效）。</summary>
    public ObservableCollection<BackdropOptionViewModel> ToastBackdropOptions { get; } = [];

    /// <summary>侧边栏当前分类：0 外观 / 1 关于。</summary>
    [ObservableProperty]
    private int _sectionIndex;

    /// <summary>「背景效果」卡片是否展开；默认收起，点击标题行后展开。</summary>
    [ObservableProperty]
    private bool _isBackdropExpanded;

    public bool IsAppearanceSection => SectionIndex == 0;

    public bool IsAboutSection => SectionIndex == 1;

    public string SectionTitle => SectionIndex == 1 ? "关于" : "外观";

    public string SectionDescription => SectionIndex == 1
        ? "版本信息与运行环境"
        : "窗口背景、主题与插入提示";

    public string VersionText { get; }

    public string SystemText { get; }

    /// <summary>当前可执行文件的构建时间，便于确认是否运行的是最新版本。</summary>
    public string BuildText { get; }

    public string SettingsFilePath { get; }

    /// <summary>设置文件所在目录，供「打开」按钮直接打开。</summary>
    public string SettingsFolderPath { get; }

    public string LogFolderPath { get; }

    partial void OnSectionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsAppearanceSection));
        OnPropertyChanged(nameof(IsAboutSection));
        OnPropertyChanged(nameof(SectionTitle));
        OnPropertyChanged(nameof(SectionDescription));
    }

    /// <summary>窗口底色不透明度（40 ~ 100）。</summary>
    [ObservableProperty]
    private double _surfaceOpacityPercent;

    /// <summary>主题：0 跟随系统 / 1 深色 / 2 浅色。</summary>
    [ObservableProperty]
    private int _themeIndex;

    /// <summary>弹窗位置：0 左下 / 1 右下 / 2 左上 / 3 右上。</summary>
    [ObservableProperty]
    private int _popupPositionIndex;

    /// <summary>弹窗停留时长（秒）。</summary>
    [ObservableProperty]
    private double _popupDurationSeconds;

    /// <summary>纯色模式下不透明度固定，滑块禁用。</summary>
    [ObservableProperty]
    private bool _opacityEnabled;

    /// <summary>插入弹窗样式：0 卡片式 / 1 整窗式。</summary>
    [ObservableProperty]
    private int _toastStyleIndex;

    /// <summary>插入弹窗底色不透明度（40 ~ 100）。</summary>
    [ObservableProperty]
    private double _toastOpacityPercent;

    public string SurfaceOpacityText => $"{SurfaceOpacityPercent:0}%";

    public string ToastOpacityText => $"{ToastOpacityPercent:0}%";

    public string PopupDurationText => $"{PopupDurationSeconds:0} 秒";

    /// <summary>只有整窗式才需要选择系统背景；卡片式不套用系统材质。</summary>
    public bool ToastBackdropVisible => ToastStyleIndex == (int)ToastStyle.Window;

    /// <summary>整窗式 + 纯色时底色固定不透明，滑块禁用。</summary>
    public bool ToastOpacityEnabled =>
        !(ToastStyleIndex == (int)ToastStyle.Window
          && BackdropService.Capabilities.Resolve(SettingsStore.Current.ToastBackdrop) == BackdropKind.Solid);

    partial void OnSurfaceOpacityPercentChanged(double value)
    {
        OnPropertyChanged(nameof(SurfaceOpacityText));

        if (_synchronizing)
            return;

        SettingsStore.Current.SurfaceOpacity = Math.Clamp(value / 100.0, 0.4, 1.0);
        SettingsStore.NotifySurface();
    }

    partial void OnToastStyleIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ToastBackdropVisible));
        OnPropertyChanged(nameof(ToastOpacityEnabled));

        if (_synchronizing || !Enum.IsDefined(typeof(ToastStyle), value))
            return;

        SettingsStore.Current.ToastStyle = (ToastStyle)value;
        SettingsStore.NotifyToast();
    }

    partial void OnToastOpacityPercentChanged(double value)
    {
        OnPropertyChanged(nameof(ToastOpacityText));

        if (_synchronizing)
            return;

        SettingsStore.Current.ToastOpacity = Math.Clamp(value / 100.0, 0.4, 1.0);
        SettingsStore.NotifyToast();
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (_synchronizing || !Enum.IsDefined(typeof(AppTheme), value))
            return;

        SettingsStore.Current.Theme = (AppTheme)value;
        SettingsStore.NotifyAppearance();
    }

    partial void OnPopupPositionIndexChanged(int value)
    {
        if (_synchronizing || !Enum.IsDefined(typeof(PopupCorner), value))
            return;

        SettingsStore.Current.PopupPosition = (PopupCorner)value;
        SettingsStore.NotifyBehavior();
    }

    partial void OnPopupDurationSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PopupDurationText));

        if (_synchronizing)
            return;

        SettingsStore.Current.PopupDurationSeconds = Math.Clamp(value, 2, 30);
        SettingsStore.NotifyBehavior();
    }

    /// <summary>切换到「外观」分类。</summary>
    [RelayCommand]
    private void ShowAppearance() => SectionIndex = 0;

    /// <summary>切换到「关于」分类。</summary>
    [RelayCommand]
    private void ShowAbout() => SectionIndex = 1;

    /// <summary>展开/收起「背景效果」选项列表。</summary>
    [RelayCommand]
    private void ToggleBackdropExpanded() => IsBackdropExpanded = !IsBackdropExpanded;

    /// <summary>恢复默认设置（背景回退为当前系统首选效果）。</summary>
    [RelayCommand]
    private void ResetDefaults()
    {
        var defaults = new AppSettings
        {
            Backdrop = BackdropService.Capabilities.PreferredDefault,
            ToastBackdrop = BackdropService.Capabilities.PreferredDefault,
        };

        SettingsStore.Reset(defaults);

        _synchronizing = true;
        SurfaceOpacityPercent = Math.Round(defaults.SurfaceOpacity * 100);
        ToastStyleIndex = (int)defaults.ToastStyle;
        ToastOpacityPercent = Math.Round(defaults.ToastOpacity * 100);
        ThemeIndex = (int)defaults.Theme;
        PopupPositionIndex = (int)defaults.PopupPosition;
        PopupDurationSeconds = defaults.PopupDurationSeconds;

        foreach (var option in BackdropOptions)
            option.IsSelected = option.Kind == defaults.Backdrop;

        foreach (var option in ToastBackdropOptions)
            option.IsSelected = option.Kind == defaults.ToastBackdrop;

        _synchronizing = false;
        UpdateOpacityAvailability();

        SettingsStore.NotifyAppearance();
        SettingsStore.NotifySurface();
        SettingsStore.NotifyBehavior();
        SettingsStore.NotifyToast();
    }

    private void SelectBackdrop(BackdropKind kind)
    {
        if (_synchronizing || !BackdropService.Capabilities.IsSupported(kind))
            return;

        SettingsStore.Current.Backdrop = kind;

        // 半透明模式没有系统材质兜底：底色接近不透明时看起来会和"纯色"一模一样。
        // 首次切换到该模式时给一个能透出桌面的浓度，之后仍可用滑块自由调整。
        if (kind == BackdropKind.Transparent && SettingsStore.Current.SurfaceOpacity > 0.6)
        {
            SettingsStore.Current.SurfaceOpacity = 0.55;
            SurfaceOpacityPercent = 55;
        }

        UpdateOpacityAvailability();
        SettingsStore.NotifyAppearance();
    }

    private void SyncSelection(BackdropKind kind)
    {
        _synchronizing = true;
        foreach (var option in BackdropOptions)
            option.IsSelected = option.Kind == kind;
        _synchronizing = false;
    }

    private void SelectToastBackdrop(BackdropKind kind)
    {
        if (_synchronizing || !BackdropService.Capabilities.IsSupported(kind))
            return;

        SettingsStore.Current.ToastBackdrop = kind;

        // 与主窗口一致：半透明且浓度过高时看起来会像纯色，先给一个能透出桌面的浓度
        if (kind == BackdropKind.Transparent && SettingsStore.Current.ToastOpacity > 0.6)
        {
            SettingsStore.Current.ToastOpacity = 0.55;
            ToastOpacityPercent = 55;
        }

        OnPropertyChanged(nameof(ToastOpacityEnabled));
        SettingsStore.NotifyToast();
    }

    private void SyncToastSelection(BackdropKind kind)
    {
        _synchronizing = true;
        foreach (var option in ToastBackdropOptions)
            option.IsSelected = option.Kind == kind;
        _synchronizing = false;
    }

    private void UpdateOpacityAvailability() =>
        OpacityEnabled = SettingsStore.Current.Backdrop != BackdropKind.Solid;
}
