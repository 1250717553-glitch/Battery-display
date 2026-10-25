using System;
using System.IO;

namespace WirelessBatteryTray.Core;

public interface ILog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? ex = null);
}

/// <summary>按天滚动的最小文件日志，单进程追加写。</summary>
public sealed class FileLog : ILog
{
    private readonly object _gate = new();

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : message + " :: " + ex);

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                AppPaths.EnsureCreated();
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                var file = Path.Combine(AppPaths.LogsDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志失败绝不拖垮主程序。
        }
    }
}
