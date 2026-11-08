using System;

namespace FluentUSB.Models;

/// <summary>弹窗停靠的屏幕角落。</summary>
public enum PopupCorner
{
    BottomLeft,
    BottomRight,
    TopLeft,
    TopRight,
}

/// <summary>窗口背景效果。</summary>
public enum BackdropKind
{
    /// <summary>不透明纯色，兼容性最好。</summary>
    Solid,

    /// <summary>半透明，无模糊。</summary>
    Transparent,

    /// <summary>Windows 模糊（Win10 1803+）。</summary>
    Blur,

    /// <summary>Windows 10 风格亚克力（Win10 1803+）。</summary>
    Acrylic10,

    /// <summary>Windows 11 系统亚克力（Win11 22H2+）。</summary>
    Acrylic11,

    /// <summary>Mica 云母（Windows 11）。</summary>
    Mica,

    /// <summary>Mica Alt 云母变体（Windows 11 22H2+）。</summary>
    MicaAlt,
}

/// <summary>主题偏好。</summary>
public enum AppTheme
{
    System,
    Dark,
    Light,
}

/// <summary>插入弹窗的绘制方式。</summary>
public enum ToastStyle
{
    /// <summary>圆角卡片 + 自绘阴影，不套用系统材质。</summary>
    Card,

    /// <summary>窗口与卡片等大，套用系统材质与系统圆角，阴影由系统绘制。</summary>
    Window,
}

/// <summary>用户设置，持久化到 %AppData%\FluentUSB\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>窗口背景效果。</summary>
    public BackdropKind Backdrop { get; set; } = BackdropKind.Mica;

    /// <summary>窗口底色不透明度（0.4 ~ 1.0），纯色模式下固定为 1。</summary>
    public double SurfaceOpacity { get; set; } = 0.86;

    /// <summary>插入弹窗的绘制方式。</summary>
    public ToastStyle ToastStyle { get; set; } = ToastStyle.Card;

    /// <summary>插入弹窗的背景效果（仅整窗式生效）。</summary>
    public BackdropKind ToastBackdrop { get; set; } = BackdropKind.Mica;

    /// <summary>插入弹窗底色不透明度（0.4 ~ 1.0）。</summary>
    public double ToastOpacity { get; set; } = 0.86;

    /// <summary>主题偏好。</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>插入弹窗停靠的角落。</summary>
    public PopupCorner PopupPosition { get; set; } = PopupCorner.BottomLeft;

    /// <summary>弹窗与屏幕工作区边缘的距离（物理像素）。</summary>
    public int PopupMargin { get; set; } = 16;

    /// <summary>弹窗自动关闭时长（秒）；鼠标悬停时暂停倒计时。</summary>
    public double PopupDurationSeconds { get; set; } = 8;
}
