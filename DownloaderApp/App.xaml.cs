using System.Configuration;
using System.Data;
using System.Windows;
using Microsoft.Extensions.Configuration;
using System.IO;
using DownloaderApp.Infrastructure; // Для FileLogger
using System.Windows.Threading; // Для DispatcherUnhandledExceptionEventArgs

namespace DownloaderApp
{
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

} 