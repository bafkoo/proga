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
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IFileLogger, FileLogger>();
                // Регистрация других сервисов
            })
            .Build();

        _logger = _host.Services.GetRequiredService<IFileLogger>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _logger.LogInfoAsync("Приложение запускается...");

        try
        {
            var mainWindow = new MainWindow();
            mainWindow.Show();
            await _logger.LogInfoAsync("MainWindow создано и показано.");
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync("Критическая ошибка при создании/отображении MainWindow", ex);
            MessageBox.Show($"Критическая ошибка при инициализации главного окна: {ex.Message}", "Ошибка UI", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }

        base.OnStartup(e);
    }

    private async void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        await _logger.LogErrorAsync("Необработанное исключение UI", e.Exception);
        MessageBox.Show($"Произошла необработанная ошибка: {e.Exception.Message}", "Критическая ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = false;
        Shutdown(-1);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _logger.LogInfoAsync("Приложение завершает работу...");
        base.OnExit(e);
    }
} 