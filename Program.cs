using Avalonia;
using FluentUSB.Services;
using System;
using System.Diagnostics;
using System.Threading;

namespace FluentUSB;

sealed class Program
{
    // 单实例互斥体：常驻程序被重复启动会产生多个托盘图标与重复弹窗
    private const string InstanceMutexName = @"Local\FluentUSB.SingleInstance";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 由「重新启动」拉起的新进程：先等旧实例完全退出，避免出现短暂双开
        WaitForPreviousInstance(args);

        using var instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // 通知已运行的实例弹出提示窗口，自身随即退出（始终只有一个实例在运行）
            Log.Info(InstanceBridge.NotifyRunningInstance()
                ? "检测到已有实例在运行，已通知该实例弹出提示"
                : "检测到已有实例在运行，但通知失败，本次启动直接退出");
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("应用异常退出", ex);
        }
    }

    /// <summary>解析 --relaunch &lt;pid&gt;：等待旧实例退出后再接管单实例互斥体。</summary>
    private static void WaitForPreviousInstance(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals("--relaunch", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!int.TryParse(args[i + 1], out var pid))
                return;

            try
            {
                using var previous = Process.GetProcessById(pid);
                previous.WaitForExit(TimeSpan.FromSeconds(10));
            }
            catch (ArgumentException)
            {
                // 旧实例已经退出，直接继续启动
            }
            catch (Exception ex)
            {
                Log.Warn($"等待旧实例退出失败：{ex.Message}");
            }

            return;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
