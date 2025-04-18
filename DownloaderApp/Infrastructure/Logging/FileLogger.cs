using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Globalization;
using System.Diagnostics;

namespace DownloaderApp.Infrastructure.Logging;

public class FileLogger : IFileLogger
{
    private readonly string _logPath;
    private readonly object _lock = new();
    private const int MaxLogFiles = 10;

    public FileLogger()
    {
        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var logDirectory = Path.Combine(appDir, "Logs");
            Debug.WriteLine($"[FileLogger] Вычисленная директория логов: {logDirectory}");
            Directory.CreateDirectory(logDirectory);
            Debug.WriteLine($"[FileLogger] Директория логов создана (или уже существует).");

            _logPath = Path.Combine(logDirectory, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            Debug.WriteLine($"[FileLogger] Полный путь к файлу лога: {_logPath}");

            CleanupOldLogs(logDirectory);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileLogger ERROR] Ошибка в конструкторе FileLogger: {ex}");
            throw;
        }
    }

    private void CleanupOldLogs(string logDirectory)
    {
        try
        {
            var logFiles = Directory.GetFiles(logDirectory, "log_*.txt")
                                  .Select(f => new FileInfo(f))
                                  .OrderByDescending(f => f.LastWriteTime)
                                  .ToList();

            if (logFiles.Count > MaxLogFiles)
            {
                var filesToDelete = logFiles.Skip(MaxLogFiles).ToList();
                foreach (var file in filesToDelete)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (IOException ex)
                    {
                        Console.WriteLine($"[FileLogger Cleanup] Error deleting log file {file.Name}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileLogger Cleanup] Error cleaning up log files: {ex.Message}");
        }
    }

    private async Task WriteToFileAsync(string message)
    {
        if (string.IsNullOrEmpty(_logPath))
        {
            Debug.WriteLine($"[FileLogger WriteToFileAsync ERROR] Log path is not initialized. Message: {message}");
            return;
        }
        
        var logLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n";
        try
        {
            await Task.Run(() =>
            {
                lock (_lock)
                {
                    File.AppendAllText(_logPath, logLine);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileLogger WriteToFileAsync ERROR] Failed to write to log file {_logPath}. Error: {ex}");
        }
    }

    private string FormatMessage(string level, string message, object[] args = null, Exception exception = null)
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
    public Task LogErrorAsync(string message, Exception exception = null, params object[] args) => WriteToFileAsync(FormatMessage("ERROR", message, args, exception));
    public async Task LogCriticalAsync(string message, Exception exception = null, params object[] args)
    {
        await WriteToFileAsync(FormatMessage("CRITICAL", message, args, exception));
    }
    public async Task LogSuccessAsync(string message, params object[] args)
    {
        await WriteToFileAsync(FormatMessage("SUCCESS", message, args, null));
    }
} 