using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DownloaderApp.Interfaces;
using DownloaderApp.Models;
using DownloaderApp.Infrastructure.Logging;
using System.Threading; // Добавляем using для SemaphoreSlim

namespace DownloaderApp.Infrastructure
{
    /// <summary>
    /// Сервис для выполнения HTTP-запросов с поддержкой отказоустойчивости
    /// </summary>
    public class HttpClientService : IHttpClientService
    {
        private static readonly HttpClient _httpClient;
        private volatile CircuitBreakerState _breakerState = CircuitBreakerState.Closed;
        private DateTime _breakerOpenUntilUtc = DateTime.MinValue;
        private volatile int _consecutive429Failures = 0;
        private const int BreakerFailureThreshold = 5;
        private readonly TimeSpan BreakerOpenDuration = TimeSpan.FromSeconds(30);
        private static readonly SemaphoreSlim _breakerSemaphore = new SemaphoreSlim(1, 1);
        private static readonly Random _random = new Random();
        private volatile int _adaptiveDelayMilliseconds = 0;
        private readonly IFileLogger _fileLogger;

        static HttpClientService()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };

            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
            _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
            _httpClient.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
            _httpClient.DefaultRequestHeaders.Add("Pragma", "no-cache");
            _httpClient.DefaultRequestHeaders.Add("Sec-Ch-Ua", "\"Chromium\";v=\"122\", \"Not(A:Brand\";v=\"24\", \"Google Chrome\";v=\"122\"");
            _httpClient.DefaultRequestHeaders.Add("Sec-Ch-Ua-Mobile", "?0");
            _httpClient.DefaultRequestHeaders.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
            _httpClient.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
        }

        public HttpClientService(IFileLogger fileLogger)
        {
            _fileLogger = fileLogger;
        }

        /// <summary>
        /// Скачивает файл по указанному URL во временный файл
        /// </summary>
        /// <param name="url">URL файла для скачивания</param>
        /// <param name="tempFilePath">Путь для сохранения</param>
        /// <param name="token">Токен отмены операции</param>
        /// <param name="progress">Прогресс скачивания</param>
        /// <returns>Результат скачивания файла</returns>
        public async Task<DownloadResult> DownloadFileAsync(string url, string tempFilePath, CancellationToken token, IProgress<double> progress = null)
        {
            // Проверка состояния Circuit Breaker перед началом запроса
            if (_breakerState == CircuitBreakerState.Open)
            {
                if (DateTime.UtcNow < _breakerOpenUntilUtc)
                {
                    return new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = $"Circuit Breaker is Open until {_breakerOpenUntilUtc}. Skipping download.",
                    };
                }
                else
                {
                    await _breakerSemaphore.WaitAsync();
                    try
                    {
                        if (_breakerState == CircuitBreakerState.Open)
                        { 
                            _breakerState = CircuitBreakerState.HalfOpen;
                            await _fileLogger.LogInfoAsync("Circuit Breaker переходит в состояние Half-Open.");
                        }
                    }
                    finally
                    {
                        _breakerSemaphore.Release();
                    }
                }
            }

            // Проверка на возможность добавить случайную задержку (для распределения нагрузки)
            int currentAdaptiveDelay = _adaptiveDelayMilliseconds;
            if (currentAdaptiveDelay > 0)
            {
                int jitter = _random.Next(0, 500); // Добавляем случайную компоненту до 0.5 сек
                await Task.Delay(currentAdaptiveDelay + jitter, token);
            }

            var downloadResult = new DownloadResult();
            
            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                
                downloadResult.StatusCode = response.StatusCode;
                
                // Обработка ответа с кодом 429 (Too Many Requests)
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfterHeader = response.Headers.RetryAfter;
                    if (retryAfterHeader != null)
                    {
                        if (retryAfterHeader.Delta.HasValue)
                        {
                            downloadResult.RetryAfterHeaderValue = retryAfterHeader.Delta.Value;
                        }
                        else if (retryAfterHeader.Date.HasValue)
                        {
                            downloadResult.RetryAfterHeaderValue = retryAfterHeader.Date.Value - DateTime.UtcNow;
                        }
                    }
                    
                    // Увеличиваем счетчик последовательных ошибок 429
                    int failures = Interlocked.Increment(ref _consecutive429Failures);
                    
                    // Если превышен порог - размыкаем цепь
                    if (failures >= BreakerFailureThreshold && _breakerState == CircuitBreakerState.Closed)
                    {
                        await _breakerSemaphore.WaitAsync();
                        try
                        {
                            if (_breakerState == CircuitBreakerState.Closed && failures >= BreakerFailureThreshold)
                            {
                                _breakerState = CircuitBreakerState.Open;
                                _breakerOpenUntilUtc = DateTime.UtcNow.Add(BreakerOpenDuration);
                                await _fileLogger.LogInfoAsync($"Circuit Breaker размыкается до {_breakerOpenUntilUtc}");
                                
                                // Увеличиваем адаптивную задержку
                                int newDelay = Math.Min(10000, currentAdaptiveDelay + 1000); // Не более 10 секунд
                                Interlocked.CompareExchange(ref _adaptiveDelayMilliseconds, newDelay, currentAdaptiveDelay);
                                await _fileLogger.LogInfoAsync($"Увеличена адаптивная задержка до {newDelay} мс из-за частых ошибок 429");
                            }
                        }
                        finally
                        {
                            _breakerSemaphore.Release();
                        }
                    }
                    
                    downloadResult.Success = false;
                    downloadResult.ErrorMessage = $"Сервер вернул код 429 (Too Many Requests). Ожидание: {downloadResult.RetryAfterHeaderValue}";
                    return downloadResult;
                }
                
                // Обработка других ошибок
                if (!response.IsSuccessStatusCode)
                {
                    downloadResult.Success = false;
                    downloadResult.ErrorMessage = $"Ошибка HTTP: {(int)response.StatusCode} {response.ReasonPhrase}";
                    return downloadResult;
                }
                
                // Получаем ожидаемый размер из заголовка Content-Length
                if (response.Content.Headers.ContentLength.HasValue)
                {
                    downloadResult.ExpectedSize = response.Content.Headers.ContentLength.Value;
                }
                
                // Создаем директорию для временного файла, если её нет
                var tempDir = Path.GetDirectoryName(tempFilePath);
                if (!string.IsNullOrEmpty(tempDir) && !Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }
                
                // Скачиваем содержимое во временный файл
                downloadResult.TempFilePath = tempFilePath;
                
                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                
                var buffer = new byte[8192];
                long totalRead = 0;
                int read;
                
                while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read, token);
                    totalRead += read;
                    
                    if (downloadResult.ExpectedSize.HasValue && progress != null)
                    {
                        double progressValue = (double)totalRead / downloadResult.ExpectedSize.Value * 100;
                        progress.Report(progressValue);
                    }
                }
                
                downloadResult.ActualSize = totalRead;
                downloadResult.Success = true;
                
                // Сбрасываем счетчик ошибок 429 при успехе
                Interlocked.Exchange(ref _consecutive429Failures, 0);
                
                // Если успешно в состоянии HalfOpen, возвращаемся в Closed
                if (_breakerState == CircuitBreakerState.HalfOpen)
                {
                    await _breakerSemaphore.WaitAsync();
                    try
                    {
                        if (_breakerState == CircuitBreakerState.HalfOpen)
                        {
                            _breakerState = CircuitBreakerState.Closed;
                            await _fileLogger.LogInfoAsync("Circuit Breaker возвращается в состояние Closed после успешного запроса.");
                        }
                    }
                    finally
                    {
                        _breakerSemaphore.Release();
                    }
                }
                
                // Уменьшаем адаптивную задержку при успехе
                if (currentAdaptiveDelay > 0)
                {
                    int newAdaptiveDelay = Math.Max(0, currentAdaptiveDelay - 500);
                    Interlocked.CompareExchange(ref _adaptiveDelayMilliseconds, newAdaptiveDelay, currentAdaptiveDelay);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                downloadResult.Success = false;
                downloadResult.ErrorMessage = ex.Message;
                
                // Проверяем, был ли создан временный файл и удаляем его при ошибке
                if (File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                        downloadResult.TempFilePath = null;
                    }
                    catch { /* Игнорируем ошибки при удалении временного файла */ }
                }
            }
            
            return downloadResult;
        }
    }
} 