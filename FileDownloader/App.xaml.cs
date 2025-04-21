using FileDownloader.Infrastructure.Services;
using FileDownloader.ViewModels; // Assuming MainViewModel is here
using FileDownloader.Views; // Assuming MainWindow is here
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Windows;

namespace FileDownloader;

public partial class App : Application
{
    private IHost? _host;

    public App()
    {
        // .NET 6+ Host application builder pattern
        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((context, config) =>
            {
                // Configure appsettings.json
                config.SetBasePath(AppContext.BaseDirectory); // Use AppContext.BaseDirectory for correct path
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            })
            .ConfigureLogging((context, logging) =>
            {
                logging.ClearProviders(); // Optional: Clear default providers if needed
                logging.AddConfiguration(context.Configuration.GetSection("Logging"));
                logging.AddDebug(); // Log to Debug output
                logging.AddConsole(); // Log to console
                // Add other providers like FileLogger if configured
            })
            .ConfigureServices((context, services) =>
            {
                // Register Configuration instance
                services.AddSingleton(context.Configuration);

                // Register Services
                services.AddSingleton<IDatabaseService, DatabaseService>();
                // Register other services...
                // services.AddTransient<IFtpService, FtpService>();

                // Register ViewModels
                services.AddSingleton<MainViewModel>(); // Register MainViewModel

                // Register Views (optional, can be handled differently)
                services.AddSingleton<MainWindow>();

            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host!.StartAsync(); // Start the host

        // Get the MainWindow from the DI container
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _host.Services.GetRequiredService<MainViewModel>(); // Set DataContext
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        using (_host)
        {
            await _host!.StopAsync(TimeSpan.FromSeconds(5)); // Allow time for graceful shutdown
        }
        base.OnExit(e);
    }
} 