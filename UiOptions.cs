using Avalonia.Controls;
using Avalonia.Styling;
using System.Collections.Generic;

namespace FluentUSB;

/// <summary>
/// 界面选项的集中挂点。
/// M0 阶段仅提供默认值；后续在此实现 深色/浅色主题切换 与 亚克力/普通背景切换。
/// </summary>
public static class UiOptions
{
    /// <summary>应用主题（预留深浅切换）。</summary>
    public static ThemeVariant Theme { get; set; } = ThemeVariant.Dark;

    /// <summary>
    /// 窗口透明度级别，按优先级回退（预留亚克力切换）。
    /// AcrylicBlur 不可用时回退到 Blur，再退化为不透明。
    /// </summary>
    public static IReadOnlyList<WindowTransparencyLevel> TransparencyHint { get; set; }
        = new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur };
}
