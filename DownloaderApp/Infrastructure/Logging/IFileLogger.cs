using System;
using System.Threading.Tasks;

namespace DownloaderApp.Infrastructure.Logging;

public interface IFileLogger
{
    Task LogDebugAsync(string message, params object[] args);
    Task LogInfoAsync(string message, params object[] args);
    Task LogWarningAsync(string message, params object[] args);
    Task LogErrorAsync(string message, Exception exception = null, params object[] args);
    Task LogCriticalAsync(string message, Exception exception = null, params object[] args);
    Task LogSuccessAsync(string message, params object[] args);
} 