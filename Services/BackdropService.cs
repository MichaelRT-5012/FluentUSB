using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using FluentUSB.Models;

namespace FluentUSB.Services;

/// <summary>允许设置窗口底色与背景效果的窗口。</summary>
internal interface IAppearanceHost
{
    Border? SurfaceHost { get; }

    /// <summary>
    /// 是否使用「插入弹窗」的独立外观设置（样式 / 背景 / 浓度）。
    /// 悬浮窗的窗口区域与卡片不一致，套用系统材质会露出矩形底板，因此它有自己的配置。
    /// </summary>
    bool UsesToastAppearance => false;
}

/// <summary>当前系统支持的窗口背景效果。</summary>
internal sealed record BackdropCapabilities(
    bool Composition,
    bool Blur,
    bool Acrylic10,
    bool Acrylic11,
    bool Mica,
    bool MicaAlt)
{
    private static readonly BackdropKind[] DefaultOrder =
    [
        BackdropKind.Mica,
        BackdropKind.Acrylic11,
        BackdropKind.Acrylic10,
        BackdropKind.Blur,
        BackdropKind.Transparent,
        BackdropKind.Solid,
    ];

    private static readonly BackdropKind[] FallbackOrder =
    [
        BackdropKind.MicaAlt,
        BackdropKind.Mica,
        BackdropKind.Acrylic11,
        BackdropKind.Acrylic10,
        BackdropKind.Blur,
        BackdropKind.Transparent,
        BackdropKind.Solid,
    ];

    public int Build => BackdropNative.BuildNumber;

    public bool IsSupported(BackdropKind kind) => kind switch
    {
        BackdropKind.Solid => true,
        BackdropKind.Transparent => Composition,
        BackdropKind.Blur => Composition && Blur,
        BackdropKind.Acrylic10 => Composition && Acrylic10,
        BackdropKind.Acrylic11 => Composition && Acrylic11,
        BackdropKind.Mica => Composition && Mica,
        BackdropKind.MicaAlt => Composition && MicaAlt,
        _ => false,
    };

    /// <summary>系统首选效果：Mica → 亚克力 11 → 亚克力 10 → 模糊 → 透明 → 纯色。</summary>
    public BackdropKind PreferredDefault =>
        DefaultOrder.FirstOrDefault(IsSupported, BackdropKind.Solid);

    /// <summary>把不受支持的设置纠正为可用的最佳效果。</summary>
    public BackdropKind Resolve(BackdropKind requested) =>
        IsSupported(requested) ? requested : PreferredDefault;

    /// <summary>某个效果应用失败时，按顺序退化到下一个可用效果。</summary>
    public BackdropKind FallbackAfter(BackdropKind kind)
    {
        var start = Array.IndexOf(FallbackOrder, kind);
        for (var i = Math.Max(start + 1, 0); i < FallbackOrder.Length; i++)
        {
            if (IsSupported(FallbackOrder[i]))
                return FallbackOrder[i];
        }

        return BackdropKind.Solid;
    }

    public string Description
    {
        get
        {
            var os = Build >= 22000 ? $"Windows 11（内部版本 {Build}）" : $"Windows 10（内部版本 {Build}）";

            if (!Composition)
                return $"{os} · 桌面合成已关闭，仅支持纯色背景";

            var supported = FallbackOrder.Where(IsSupported).Select(BackdropCatalog.Title).ToArray();
            return $"{os} · 可用：{string.Join(" / ", supported)}";
        }
    }

    public static BackdropCapabilities Detect()
    {
        var build = BackdropNative.BuildNumber;
        var composition = BackdropNative.IsCompositionEnabled;
        var wca = BackdropNative.IsCompositionAttributeAvailable;

        // 模糊与亚克力 10 依赖 SetWindowCompositionAttribute，只在 Windows 10 上有效；
        // Windows 11（22000+）已移除该效果，调用后只会得到一层灰色蒙版，因此直接禁用。
        var legacyEffects = wca && build >= 17134 && build < 22000;

        return new BackdropCapabilities(
            Composition: composition,
            Blur: legacyEffects,
            Acrylic10: legacyEffects,
            Acrylic11: composition && build >= 22621,
            Mica: composition && build >= 22000,
            MicaAlt: composition && build >= 22621);
    }
}

/// <summary>背景效果在设置界面的文案。</summary>
internal static class BackdropCatalog
{
    public static string Title(BackdropKind kind) => kind switch
    {
        BackdropKind.Solid => "纯色",
        BackdropKind.Transparent => "半透明",
        BackdropKind.Blur => "模糊",
        BackdropKind.Acrylic10 => "亚克力 10",
        BackdropKind.Acrylic11 => "亚克力 11",
        BackdropKind.Mica => "Mica 云母",
        BackdropKind.MicaAlt => "Mica Alt 云母",
        _ => kind.ToString(),
    };

    public static string Description(BackdropKind kind) => kind switch
    {
        BackdropKind.Solid => "不透明底色，兼容性最好",
        BackdropKind.Transparent => "无模糊，直接透出桌面",
        BackdropKind.Blur => "高斯模糊桌面背景",
        BackdropKind.Acrylic10 => "Windows 10 风格亚克力，带噪点纹理",
        BackdropKind.Acrylic11 => "Windows 11 系统亚克力",
        BackdropKind.Mica => "跟随桌面壁纸色调",
        BackdropKind.MicaAlt => "Mica 标签页变体，色调更明显",
        _ => string.Empty,
    };

    public static string Requirement(BackdropKind kind) => kind switch
    {
        BackdropKind.Blur or BackdropKind.Acrylic10 => "仅 Windows 10 支持",
        BackdropKind.Mica => "需要 Windows 11",
        BackdropKind.Acrylic11 or BackdropKind.MicaAlt => "需要 Windows 11 22H2 及以上",
        _ => string.Empty,
    };
}

/// <summary>把设置中的背景与底色应用到窗口。</summary>
internal static class BackdropService
{
    public static BackdropCapabilities Capabilities { get; } = BackdropCapabilities.Detect();

    /// <summary>启动时把配置里"当前系统不支持"的效果纠正为可用效果并落盘。</summary>
    public static void EnsureSupportedSettings()
    {
        var settings = SettingsStore.Current;
        var resolved = Capabilities.Resolve(settings.Backdrop);
        if (resolved != settings.Backdrop)
        {
            Log.Info($"配置的背景效果 {settings.Backdrop} 在当前系统不可用，改用 {resolved}");
            settings.Backdrop = resolved;
            SettingsStore.SaveNow();
        }
    }

    /// <summary>应用原生背景效果与底色；需在窗口已显示（有窗口句柄）后调用。</summary>
    public static void Apply(Window window)
    {
        if (window.TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero)
            return;

        var toast = window is IAppearanceHost host && host.UsesToastAppearance;
        var kind = toast
            ? Capabilities.Resolve(SettingsStore.Current.ToastBackdrop)
            : Capabilities.Resolve(SettingsStore.Current.Backdrop);

        // 卡片式悬浮窗的窗口比卡片大一圈，绝不能套用系统材质，否则会露出矩形底板
        var wantsNative = !toast || SettingsStore.Current.ToastStyle == ToastStyle.Window;

        if (wantsNative)
        {
            if (!BackdropNative.Apply(handle.Handle, kind, IsDark()))
            {
                var fallback = Capabilities.FallbackAfter(kind);
                Log.Warn($"背景效果 {kind} 应用失败，回退到 {fallback}");
                BackdropNative.Apply(handle.Handle, fallback, IsDark());
                kind = fallback;
            }
        }
        else
        {
            // 从整窗式切回卡片式时，撤掉之前扩展的 DWM 边框，避免材质残留
            BackdropNative.Reset(handle.Handle);
        }

        ApplySurface(window, kind);
    }

    /// <summary>只更新窗口底色（拖动不透明度滑块时使用，避免反复调用原生接口）。</summary>
    public static void ApplySurface(Window window, BackdropKind? kind = null)
    {
        if (window is not IAppearanceHost host || host.SurfaceHost is not { } surface)
            return;

        var toast = host.UsesToastAppearance;
        var effective = kind ?? (toast
            ? Capabilities.Resolve(SettingsStore.Current.ToastBackdrop)
            : Capabilities.Resolve(SettingsStore.Current.Backdrop));

        surface.Background = toast ? CreateToastBrush(effective) : CreateSurfaceBrush(effective);
    }

    public static IBrush CreateSurfaceBrush(BackdropKind kind)
    {
        var opacity = kind == BackdropKind.Solid ? 1.0 : SettingsStore.Current.SurfaceOpacity;
        return new SolidColorBrush(SurfaceColor(), opacity);
    }

    /// <summary>悬浮窗卡片底色：卡片式始终半透明；整窗式纯色时完全不透明。</summary>
    public static IBrush CreateToastBrush(BackdropKind kind)
    {
        var settings = SettingsStore.Current;
        var opacity = settings.ToastStyle == ToastStyle.Window && kind == BackdropKind.Solid
            ? 1.0
            : settings.ToastOpacity;
        return new SolidColorBrush(SurfaceColor(), opacity);
    }

    private static Color SurfaceColor() =>
        IsDark() ? Color.FromRgb(0x17, 0x17, 0x17) : Color.FromRgb(0xF9, 0xF9, 0xF9);

    public static bool IsDark() =>
        Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
}
