using System;
using System.IO;

namespace FluentUSB.Services;

/// <summary>
/// 极简文件日志：写入 %AppData%\FluentUSB\logs，按天分文件。
/// 常驻程序出问题时用户可把日志发回排查；任何写日志失败都不得影响主流程。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluentUSB", "logs");

    /// <summary>日志目录（设置页「关于」中展示）。</summary>
    public static string DirectoryPath => LogDirectory;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            var file = Path.Combine(LogDirectory, $"fluentusb-{DateTime.Now:yyyyMMdd}.log");

            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(file, line);
            }
        }
        catch
        {
            // 日志本身失败时静默忽略，避免掩盖真正的问题
        }
    }
}
