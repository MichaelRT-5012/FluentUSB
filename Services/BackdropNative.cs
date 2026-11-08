using System;
using System.Runtime.InteropServices;
using FluentUSB.Models;

namespace FluentUSB.Services;

/// <summary>
/// 窗口背景相关的原生调用。
/// - Mica / Mica Alt / 亚克力 11：DWM 系统背景（DWMWA_SYSTEMBACKDROP_TYPE）
/// - 模糊 / 亚克力 10：Win32 SetWindowCompositionAttribute 强调色策略
/// </summary>
internal static class BackdropNative
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaMicaEffect = 1029;

    private const int DwmsbtNone = 1;
    private const int DwmsbtMainWindow = 2;
    private const int DwmsbtTransientWindow = 3;
    private const int DwmsbtTabbedWindow = 4;

    private const int DwmwcpDefault = 0;
    private const int DwmwcpRound = 2;

    private const int WcaAccentPolicy = 19;

    private const int AccentDisabled = 0;
    private const int AccentBlurBehind = 3;
    private const int AccentAcrylicBlurBehind = 4;

    private static readonly Lazy<bool> WindowCompositionAttributeAvailable =
        new(() => ProbeExport("user32.dll", "SetWindowCompositionAttribute"));

    /// <summary>当前 Windows 内部版本号，如 26200。</summary>
    public static int BuildNumber { get; } = GetBuildNumber();

    /// <summary>桌面窗口管理器合成是否可用（关闭 DWM 时所有透明/模糊效果都不可用）。</summary>
    public static bool IsCompositionEnabled { get; } = GetCompositionEnabled();

    /// <summary>SetWindowCompositionAttribute 是否可用（Win10 1803+ 的模糊/亚克力依赖它）。</summary>
    public static bool IsCompositionAttributeAvailable => WindowCompositionAttributeAvailable.Value;

    /// <summary>应用指定背景效果；返回是否成功。</summary>
    public static bool Apply(IntPtr hwnd, BackdropKind kind, bool dark)
    {
        SetDarkMode(hwnd, dark);
        Reset(hwnd);

        return kind switch
        {
            BackdropKind.Solid or BackdropKind.Transparent => true,
            BackdropKind.Blur => SetAccent(hwnd, AccentBlurBehind, 0x00000000),
            BackdropKind.Acrylic10 => SetAccent(hwnd, AccentAcrylicBlurBehind, 0x01000000),
            BackdropKind.Acrylic11 => ExtendFrame(hwnd) & SetSystemBackdrop(hwnd, DwmsbtTransientWindow),
            BackdropKind.Mica => ExtendFrame(hwnd) & SetMicaBackdrop(hwnd),
            BackdropKind.MicaAlt => ExtendFrame(hwnd) & SetSystemBackdrop(hwnd, DwmsbtTabbedWindow),
            _ => true,
        };
    }

    /// <summary>清除上一次应用的原生效果，避免切换时残留。</summary>
    public static void Reset(IntPtr hwnd)
    {
        // 关闭强调色策略（模糊/亚克力）
        SetAccent(hwnd, AccentDisabled, 0);

        // 关闭 DWM 系统背景（None）
        SetDwmAttribute(hwnd, DwmwaSystemBackdropType, DwmsbtNone);

        // 关闭 Win11 21H2 的旧版 Mica 开关
        SetDwmAttribute(hwnd, DwmwaMicaEffect, 0);

        // 系统背景类型置 None 后，之前扩展过的 DWM 边框仍会保留整窗材质，
        // 导致切回半透明/纯色时依然显示为“纯色”。这里把边框收回到 0，
        // 让窗口恢复普通的客户区绘制，半透明才能重新透出桌面。
        ContractFrame(hwnd);
    }

    /// <summary>让窗口标题栏跟随应用深浅主题（Mica/亚克力下尤其明显）。</summary>
    public static void SetDarkMode(IntPtr hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        if (!SetDwmAttribute(hwnd, DwmwaUseImmersiveDarkMode, value))
            SetDwmAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, value);
    }

    /// <summary>
    /// 设置窗口是否使用系统圆角（Windows 11 21H2+）。
    /// 整窗式悬浮窗让窗口贴着内容，需要系统圆角把系统材质裁成圆角。
    /// </summary>
    public static bool SetRoundedCorners(IntPtr hwnd, bool rounded) =>
        SetDwmAttribute(hwnd, DwmwaWindowCornerPreference, rounded ? DwmwcpRound : DwmwcpDefault);

    private static bool SetMicaBackdrop(IntPtr hwnd)
    {
        // 22H2（22621）起使用正式的系统背景类型；21H2（22000）只能使用旧版开关
        return BuildNumber >= 22621
            ? SetSystemBackdrop(hwnd, DwmsbtMainWindow)
            : SetDwmAttribute(hwnd, DwmwaMicaEffect, 1);
    }

    private static bool SetSystemBackdrop(IntPtr hwnd, int backdropType) =>
        SetDwmAttribute(hwnd, DwmwaSystemBackdropType, backdropType);

    private static bool SetDwmAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            var hr = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
            return hr >= 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ExtendFrame(IntPtr hwnd)
    {
        try
        {
            // 负边距表示把整个客户区都扩展进 DWM 边框，系统背景才能绘制在客户区
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            return DwmExtendFrameIntoClientArea(hwnd, ref margins) >= 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>把 DWM 边框收回，撤销 ExtendFrame 留下的整窗玻璃区域。</summary>
    private static bool ContractFrame(IntPtr hwnd)
    {
        try
        {
            var margins = new Margins();
            return DwmExtendFrameIntoClientArea(hwnd, ref margins) >= 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SetAccent(IntPtr hwnd, int state, int gradientColor)
    {
        if (!IsCompositionAttributeAvailable)
            return state == AccentDisabled;

        var policy = new AccentPolicy
        {
            AccentState = state,
            AccentFlags = state == AccentDisabled ? 0 : 2,
            GradientColor = gradientColor,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(policy, buffer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                SizeOfData = size,
            };

            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int GetBuildNumber()
    {
        try
        {
            return Environment.OSVersion.Version.Build;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool GetCompositionEnabled()
    {
        try
        {
            return DwmIsCompositionEnabled(out var enabled) >= 0 && enabled != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool ProbeExport(string module, string export)
    {
        try
        {
            return NativeLibrary.TryGetExport(NativeLibrary.Load(module), export, out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    #region Win32 P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled(out int enabled);

    [DllImport("user32.dll")]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    #endregion
}
