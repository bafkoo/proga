using System;
using System.Net;

namespace DownloaderApp.Models
{
    /// <summary>
    /// Класс для хранения результатов загрузки файла
    /// </summary>
    public class DownloadResult
    {
        /// <summary>
        /// Успешно ли завершена загрузка
        /// </summary>
        public bool Success { get; set; }
        
        /// <summary>
        /// Ожидаемый размер из Content-Length заголовка (может быть null)
        /// </summary>
        public long? ExpectedSize { get; set; }
        
        /// <summary>
        /// Фактический размер скачанного файла
        /// </summary>
        public long ActualSize { get; set; }
        
        /// <summary>
        /// HTTP статус ответа
        /// </summary>
        public HttpStatusCode? StatusCode { get; set; }
        
        /// <summary>
        /// Сообщение об ошибке при неуспешной загрузке
        /// </summary>
        public string ErrorMessage { get; set; }
        
        /// <summary>
        /// Путь к временному файлу (если он остался)
        /// </summary>
        public string TempFilePath { get; set; }
        
        /// <summary>
        /// Значение заголовка Retry-After при ограничении скорости
        /// </summary>
        public TimeSpan? RetryAfterHeaderValue { get; set; }
    }
} 