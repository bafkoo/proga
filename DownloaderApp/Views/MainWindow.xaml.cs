using System.Windows;
using FileDownloader.ViewModels;
using MahApps.Metro.Controls;
using DownloaderApp.Infrastructure.Logging;
using System.Threading.Tasks;

namespace FileDownloader.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : MetroWindow
{
    public MainWindow()
    {
        InitializeComponent();
        _ = SetDataContextAsync();
    }

    private async Task SetDataContextAsync()
    {
        IFileLogger fileLogger = null;
        try
        {
            fileLogger = new FileLogger();
            await fileLogger.LogInfoAsync("MainWindow: FileLogger создан.");
            await fileLogger.LogInfoAsync("MainWindow: Запуск SetDataContextAsync");

            var viewModel = await DownloaderViewModel.CreateAsync(fileLogger);
            await fileLogger.LogInfoAsync("MainWindow: DownloaderViewModel создан успешно");
            
            this.DataContext = viewModel;
            await fileLogger.LogInfoAsync("MainWindow: DataContext установлен");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"!!! КРИТИЧЕСКАЯ ОШИБКА SetDataContextAsync: {ex}");
            try 
            { 
                 await fileLogger?.LogErrorAsync("Критическая ошибка при инициализации MainWindow", ex);
            }
            catch { /* Игнорируем ошибки записи в лог на этом этапе */ }

            MessageBox.Show($"Критическая ошибка при запуске приложения:\n{ex.Message}\n\nПодробности смотрите в файле лога.", 
                            "Ошибка инициализации", 
                            MessageBoxButton.OK, 
                            MessageBoxImage.Error);
            
            Application.Current.Shutdown(-1); 
        }
    }
} 