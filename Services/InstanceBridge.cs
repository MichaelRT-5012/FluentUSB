using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace FluentUSB.Services;

/// <summary>
/// 单实例通信：第二个进程通过命名管道通知已运行的实例，由它弹出提示窗口。
/// 管道名带上用户与会话号，与 Local\ 互斥体的作用域保持一致。
/// </summary>
internal static class InstanceBridge
{
    private const string Signal = "second-instance";

    private static string PipeName =>
        $"FluentUSB.InstanceBridge.{Environment.UserName}.{Process.GetCurrentProcess().SessionId}";

    /// <summary>已运行的实例调用：后台循环等待第二个实例发来的信号。</summary>
    public static void StartListening(Action onSecondInstance)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    await server.WaitForConnectionAsync();

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    if (await reader.ReadLineAsync() == Signal)
                    {
                        Log.Info("收到第二个实例的通知，弹出提示窗口");
                        Dispatcher.UIThread.Post(onSecondInstance);
                    }
                }
                catch (Exception ex)
                {
                    // 管道暂不可用（例如上一个连接尚未完全释放）时稍后重试，
                    // 不能让监听任务静默退出，否则之后重复启动将没有任何反馈
                    Log.Warn($"实例通信失败：{ex.Message}");
                    await Task.Delay(1000);
                }
            }
        });
    }

    /// <summary>第二个实例调用：通知已运行的实例；连不上时返回 false。</summary>
    public static bool NotifyRunningInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);

            // 首个实例的监听要等 Avalonia 初始化完成，这里给 2 秒连接窗口
            client.Connect(2000);

            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.WriteLine(Signal);
            writer.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"通知已有实例失败：{ex.Message}");
            return false;
        }
    }
}
