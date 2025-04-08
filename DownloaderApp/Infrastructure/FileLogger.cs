using System;
using System.IO;
using System.Text;
using System.Threading;

namespace FileDownloader.Infrastructure;

/// <summary>
/// Простой статический логгер для записи в файл.
/// </summary>
public static class FileLogger
{
    private static string _logFilePath = null;
    private static readonly object _lockObject = new object(); // Для потокобезопасности
    private static bool _isInitialized = false;

    /// <summary>
    /// Инициализирует логгер, задавая путь к файлу.
    /// Вызывать один раз при старте приложения.
    /// </summary>
    /// <param name="logDirectory">Директория для лог-файлов.</param>
    /// <param name="logFileNamePrefix">Префикс имени файла (например, "DownloaderAppLog_").</param>
    public static void Initialize(string logDirectory = "Logs", string logFileNamePrefix = "DownloaderAppLog_")
    {
        if (_isInitialized) return; // Инициализировать только один раз

        try
        {
            // Создаем директорию, если она не существует
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            // Формируем имя файла с датой
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string logFileName = $"{logFileNamePrefix}{timestamp}.log";
            _logFilePath = Path.Combine(logDirectory, logFileName);

            // Записываем стартовое сообщение
            Log($"--- Логгер инициализирован. Файл: {_logFilePath} ---");
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            // Ошибка инициализации логгера - выводим в консоль/отладку
            Console.WriteLine($"ОШИБКА ИНИЦИАЛИЗАЦИИ ЛОГГЕРА: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"ОШИБКА ИНИЦИАЛИЗАЦИИ ЛОГГЕРА: {ex.Message}");
            _logFilePath = null; // Сбрасываем путь, чтобы не пытаться писать
            _isInitialized = false;
        }
    }

    /// <summary>
    /// Записывает сообщение в лог-файл.
    /// </summary>
    /// <param name="message">Сообщение для записи.</param>
    public static void Log(string message)
    {
        // Не пытаемся писать, если не инициализирован или произошла ошибка инициализации
        if (!_isInitialized || string.IsNullOrEmpty(_logFilePath))
        {
            Console.WriteLine($"ЛОГГЕР НЕ ИНИЦИАЛИЗИРОВАН: {message}"); // Дублируем в консоль
            return;
        }

        try
        {
            // Блокируем доступ для других потоков на время записи
            lock (_lockObject)
            {
                // Используем StreamWriter с Append=true и указываем кодировку UTF8
                using (StreamWriter writer = new StreamWriter(_logFilePath, true, Encoding.UTF8))
                {
                    writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {message}");
                }
            }
        }
        catch (Exception ex)
        {
            // Ошибка записи в лог - выводим в консоль/отладку
            Console.WriteLine($"ОШИБКА ЗАПИСИ В ЛОГ: {ex.Message} | Сообщение: {message}");
            System.Diagnostics.Debug.WriteLine($"ОШИБКА ЗАПИСИ В ЛОГ: {ex.Message}");
            // Можно попытаться записать в другой файл или предпринять другие действия
        }
    }
} 