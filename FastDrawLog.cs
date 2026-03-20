using Godot;
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace FastDrawImg;

public static class FastDrawLog
{
    private static readonly object SyncRoot = new();
    private static string? _logPath;

    public static bool IsDebugEnabled { get; private set; }

    public static void Configure(string baseDirectory, bool enabled)
    {
        IsDebugEnabled = enabled;
        _logPath = string.IsNullOrWhiteSpace(baseDirectory)
            ? null
            : Path.Combine(baseDirectory, "FastDrawImg.debug.log");

        if (!IsDebugEnabled || string.IsNullOrWhiteSpace(_logPath))
            return;

        WriteLine("DEBUG", "==== session start ====");
    }

    public static void Debug(string message)
    {
        if (!IsDebugEnabled)
            return;

        WriteLine("DEBUG", message);
    }

    public static void Warn(string message)
    {
        GD.PushWarning("[FastDrawImg] " + message);
        if (IsDebugEnabled)
            WriteLine("WARN", message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        string fullMessage = exception == null ? message : $"{message}: {exception}";
        GD.PushError("[FastDrawImg] " + fullMessage);
        if (IsDebugEnabled)
            WriteLine("ERROR", fullMessage);
    }

    private static void WriteLine(string level, string message)
    {
        if (string.IsNullOrWhiteSpace(_logPath))
            return;

        string timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        string line = $"{timestamp} [{level}] {message}{System.Environment.NewLine}";

        lock (SyncRoot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, line, Encoding.UTF8);
        }
    }
}
