using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace DownloaderApp.Interfaces;

public interface IDocumentDownloadService
{
    /// <summary>
    /// Выполняет скачивание, обновление флага и регистрацию метаданных для всех файлов за период.
    /// </summary>
    Task DownloadAndRegisterFilesAsync(DateTime dtB, DateTime dtE, int themeId, CancellationToken token);
} 