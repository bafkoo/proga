using System.Configuration;
using System.Data;
using System.Windows;
using Microsoft.Extensions.Configuration;
using System.IO;
using DownloaderApp.Infrastructure; // Added for FileLogger
using System.Windows.Threading; // Для DispatcherUnhandledExceptionEventArgs
using System.Linq; // Добавлено для очистки логов
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FileDownloader.ViewModels; // Обновляем using
using FileDownloader.Views; // Обновляем using

namespace FileDownloader; // Обновляем пространство имен

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static IConfiguration Configuration { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Инициализация логгера
        // Логи будут сохраняться в подпапку Logs рядом с exe
        FileLogger.Initialize(); 
        FileLogger.Log("Приложение запускается...");

        // Очистка старых лог-файлов
        try
        {
            string logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            if (Directory.Exists(logDirectory))
            {
                var logFiles = Directory.GetFiles(logDirectory, "*.log")
                                        .Select(f => new FileInfo(f))
                                        .OrderBy(f => f.CreationTimeUtc)
                                        .ToList();

                int maxLogFiles = 10; // Максимальное количество лог-файлов
                if (logFiles.Count > maxLogFiles)
                {
                    int filesToDeleteCount = logFiles.Count - maxLogFiles;
                    FileLogger.Log($"Обнаружено {logFiles.Count} лог-файлов. Удаление {filesToDeleteCount} самых старых...");
                    for (int i = 0; i < filesToDeleteCount; i++)
                    {
                        try
                        {
                            logFiles[i].Delete();
                            FileLogger.Log($"Удален старый лог-файл: {logFiles[i].Name}");
                        }
                        catch (Exception deleteEx)
                        {
                            FileLogger.Log($"Не удалось удалить лог-файл {logFiles[i].Name}: {deleteEx.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception cleanupEx)
        {
            FileLogger.Log($"Ошибка при очистке старых лог-файлов: {cleanupEx.Message}");
            // Не прерываем запуск приложения из-за ошибки очистки логов
        }

        // Добавляем обработчик необработанных исключений UI-потока
        this.DispatcherUnhandledException += App_DispatcherUnhandledException;

        // Загружаем конфигурацию
        try
        {
            var builder = new ConfigurationBuilder()
                 .SetBasePath(Directory.GetCurrentDirectory())
                 .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            Configuration = builder.Build();
            FileLogger.Log("Конфигурация загружена.");
        }
        catch (Exception configEx)
        {   // Оставляем базовую обработку ошибки конфигурации
            FileLogger.Log($"КРИТИЧЕСКАЯ ОШИБКА при чтении конфигурации: {configEx}");
            MessageBox.Show($"Критическая ошибка при чтении конфигурации: {configEx.Message}", "Ошибка конфигурации", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }
        
        // Вызываем базовый метод
        base.OnStartup(e);

        // Просто создаем и показываем окно БЕЗ дополнительных try-catch и логирования здесь
        // Обернем создание и показ окна в try-catch для логирования возможных ошибок инициализации
        try
        {
            var mainWindow = new Views.MainWindow();
            mainWindow.Show();
            FileLogger.Log("MainWindow создано и показано."); // Добавим лог после Show()
        }
        catch (Exception ex)
        {
            FileLogger.Log($"КРИТИЧЕСКАЯ ОШИБКА при создании/отображении MainWindow: {ex}");
            MessageBox.Show($"Критическая ошибка при инициализации главного окна: {ex.Message}", "Ошибка UI", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Логируем необработанное исключение
        FileLogger.Log($"НЕОБРАБОТАННОЕ ИСКЛЮЧЕНИЕ UI: {e.Exception}");

        // Показываем сообщение пользователю (опционально, но полезно для отладки)
        MessageBox.Show($"Произошла необработанная ошибка: {e.Exception.Message}", "Критическая ошибка", MessageBoxButton.OK, MessageBoxImage.Error);

        // Предотвращаем аварийное завершение приложения (установите в true, если хотите попробовать продолжить работу)
        // В данном случае лучше завершить приложение, т.к. ошибка может быть критической
        e.Handled = false; // Оставляем false, чтобы приложение завершилось после сообщения
        Shutdown(-1); // Принудительное завершение после логирования и сообщения
    }

    // Можно настроить создание ViewModel здесь, если использовать IoC контейнер
    // protected override void OnStartup(StartupEventArgs e)
    // {
    //     base.OnStartup(e);
    //     // ... настройка IoC ...
    //     var mainWindow = new Views.MainWindow();
    //     // mainWindow.DataContext = ... // Получение ViewModel из IoC
    //     mainWindow.Show();
    // }
} 