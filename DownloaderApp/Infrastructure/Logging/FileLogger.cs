using System;
using System.IO;
using System.Threading.Tasks;
using System.Globalization;

namespace DownloaderApp.Infrastructure.Logging;

public class FileLogger : IFileLogger
{
    private readonly string _logPath;
    private readonly object _lock = new();

    public FileLogger()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var logDirectory = Path.Combine(appDataPath, "FileDownloader", "Logs");
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, $"log_{DateTime.Now:yyyy-MM-dd}.txt");
    }

    private async Task WriteToFileAsync(string message)
    {
        var logLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n";
        await Task.Run(() =>
        {
            lock (_lock)
            {
                File.AppendAllText(_logPath, logLine);
            }
        });
    }

    private string FormatMessage(string level, string message, object[]? args = null, Exception? exception = null)
    {
        var formatted = args != null && args.Length > 0 ? string.Format(CultureInfo.InvariantCulture, message, args) : message;
        if (exception != null)
        {
            formatted += $"\nException: {exception.Message}\nStackTrace: {exception.StackTrace}";
        }
        return $"[{level}] {formatted}";
    }

    public Task LogDebugAsync(string message, params object[] args) => WriteToFileAsync(FormatMessage("DEBUG", message, args));
    public Task LogInfoAsync(string message, params object[] args) => WriteToFileAsync(FormatMessage("INFO", message, args));
    public Task LogWarningAsync(string message, params object[] args) => WriteToFileAsync(FormatMessage("WARN", message, args));
    public Task LogErrorAsync(string message, Exception? exception = null, params object[] args) => WriteToFileAsync(FormatMessage("ERROR", message, args, exception));
    public Task LogCriticalAsync(string message, Exception? exception = null, params object[] args) => WriteToFileAsync(FormatMessage("CRITICAL", message, args, exception));
} 