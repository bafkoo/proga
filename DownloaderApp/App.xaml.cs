using System.Configuration;
using System.Data;
using System.Windows;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Windows.Threading;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FileDownloader.ViewModels;
using FileDownloader.Views;
using DownloaderApp.Infrastructure.Logging;
using System.Threading.Tasks;

namespace FileDownloader;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private readonly IFileLogger _logger;
    private readonly IHost _host;

    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IFileLogger, FileLogger>();
                // Регистрация других сервисов

                // Регистрируем ViewModel (если он нужен где-то еще через DI)
                // services.AddSingleton<DownloaderViewModel>(); // Пока неясно, нужно ли это

                // Регистрируем MainWindow и позволяем DI внедрить IFileLogger
                services.AddSingleton<MainWindow>();
            })
            .Build();

        _logger = _host.Services.GetRequiredService<IFileLogger>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _logger.LogInfoAsync("Приложение запускается...");

        try
        {
            await _logger.LogDebugAsync("OnStartup: Начало асинхронной инициализации.");

            // Вручную создаем ViewModel асинхронно
            await _logger.LogDebugAsync("OnStartup: Перед DownloaderViewModel.CreateAsync.");
            var viewModel = await DownloaderViewModel.CreateAsync(_logger); // Передаем _logger
            await _logger.LogDebugAsync("OnStartup: После DownloaderViewModel.CreateAsync.");

            // Получаем MainWindow из DI контейнера
            await _logger.LogDebugAsync("OnStartup: Перед GetRequiredService<MainWindow>.");
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            await _logger.LogDebugAsync("OnStartup: После GetRequiredService<MainWindow>.");

            // Устанавливаем DataContext вручную
            mainWindow.DataContext = viewModel;
            await _logger.LogDebugAsync("OnStartup: DataContext установлен.");

            mainWindow.Show();
            await _logger.LogInfoAsync("MainWindow создано и показано.");
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync("Критическая ошибка во время OnStartup", ex); // Уточнено сообщение
            MessageBox.Show($"Критическая ошибка при инициализации приложения: {ex.Message}", "Критическая ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private async void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        await _logger.LogErrorAsync("Необработанное исключение UI", e.Exception);
        MessageBox.Show($"Произошла необработанная ошибка: {e.Exception.Message}", "Критическая ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(-1);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _logger.LogInfoAsync("Приложение завершает работу...");
        base.OnExit(e);
    }
} 