using System.Windows;
using DownloaderApp.ViewModels;
using MahApps.Metro.Controls;
using DownloaderApp.Infrastructure.Logging;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FileDownloader.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : MetroWindow
{
    private readonly IFileLogger _logger;

    public MainWindow(IFileLogger logger)
    {
        _logger = logger;
        _ = _logger?.LogDebugAsync("MainWindow: Вход в конструктор.");

        InitializeComponent();

        _ = _logger?.LogDebugAsync("MainWindow: После InitializeComponent.");

        _ = _logger?.LogDebugAsync("MainWindow: Выход из конструктора.");
    }
} 