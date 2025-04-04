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
            var mainWindow = new Views.MainWindow();
            mainWindow.Show();
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