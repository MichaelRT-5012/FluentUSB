using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using FluentUSB.Models;

namespace FluentUSB.Services;

/// <summary>
/// 设置的加载、保存与变更通知。
/// 变更分三类：外观（背景/主题，需要重设原生窗口效果）、底色浓度、弹窗行为。
/// 写入带 400ms 防抖，避免拖动滑块时频繁写盘。
/// </summary>
internal static class SettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FluentUSB");

    private static readonly string FilePath = Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>设置文件位置（设置页「关于」中展示）。</summary>
    public static string SettingsFilePath => FilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static DispatcherTimer? _saveTimer;

    public static AppSettings Current { get; private set; } = new();

    /// <summary>背景效果或主题发生变化（需要重新应用原生窗口效果）。</summary>
    public static event Action? AppearanceChanged;

    /// <summary>只有窗口底色浓度变化（不需要重设原生效果）。</summary>
    public static event Action? SurfaceChanged;

    /// <summary>弹窗位置/时长变化。</summary>
    public static event Action? BehaviorChanged;

    /// <summary>插入弹窗外观（样式/背景/浓度）变化。</summary>
    public static event Action? ToastChanged;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                Current = Sanitize(JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings());
            }

            Log.Info($"设置已加载：背景={Current.Backdrop}，主题={Current.Theme}");
        }
        catch (Exception ex)
        {
            Log.Error("加载设置失败，使用默认值", ex);
            Current = new AppSettings();
        }
    }

    /// <summary>用给定设置替换当前设置并立即落盘（用于恢复默认）。</summary>
    public static void Reset(AppSettings settings)
    {
        Current = Sanitize(settings);
        SaveNow();
    }

    public static void NotifyAppearance()
    {
        ScheduleSave();
        AppearanceChanged?.Invoke();
    }

    public static void NotifySurface()
    {
        ScheduleSave();
        SurfaceChanged?.Invoke();
    }

    public static void NotifyBehavior()
    {
        ScheduleSave();
        BehaviorChanged?.Invoke();
    }

    public static void NotifyToast()
    {
        ScheduleSave();
        ToastChanged?.Invoke();
    }

    /// <summary>立即写入磁盘（防抖定时器会先取消）。</summary>
    public static void SaveNow()
    {
        _saveTimer?.Stop();

        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("保存设置失败", ex);
        }
    }

    public static AppSettings Sanitize(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.Backdrop))
            settings.Backdrop = BackdropKind.Solid;

        if (!Enum.IsDefined(settings.ToastBackdrop))
            settings.ToastBackdrop = BackdropKind.Mica;

        if (!Enum.IsDefined(settings.ToastStyle))
            settings.ToastStyle = ToastStyle.Card;

        if (!Enum.IsDefined(settings.Theme))
            settings.Theme = AppTheme.System;

        if (!Enum.IsDefined(settings.PopupPosition))
            settings.PopupPosition = PopupCorner.BottomLeft;

        settings.SurfaceOpacity = Math.Clamp(settings.SurfaceOpacity, 0.4, 1.0);
        settings.ToastOpacity = Math.Clamp(settings.ToastOpacity, 0.4, 1.0);
        settings.PopupMargin = Math.Clamp(settings.PopupMargin, 4, 64);
        settings.PopupDurationSeconds = Math.Clamp(settings.PopupDurationSeconds, 2, 60);

        return settings;
    }

    private static void ScheduleSave()
    {
        if (_saveTimer is null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer?.Stop();
                SaveNow();
            };
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }
}
