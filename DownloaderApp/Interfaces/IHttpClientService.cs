using System;
using System.Threading;
using System.Threading.Tasks;
using DownloaderApp.Models;

namespace DownloaderApp.Interfaces
{
    /// <summary>
    /// Интерфейс для сервиса HTTP-запросов с поддержкой отказоустойчивости
    /// </summary>
    public interface IHttpClientService
    {
        /// <summary>
        /// Скачивает файл по указанному URL во временный файл
        /// </summary>
        /// <param name="url">URL файла для скачивания</param>
        /// <param name="tempFilePath">Путь для сохранения</param>
        /// <param name="token">Токен отмены операции</param>
        /// <param name="progress">Прогресс скачивания</param>
        /// <returns>Результат скачивания файла</returns>
        Task<DownloadResult> DownloadFileAsync(string url, string tempFilePath, CancellationToken token, IProgress<double> progress = null);
    }
} 