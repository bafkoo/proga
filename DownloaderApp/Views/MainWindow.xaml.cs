using System.Windows;
using FileDownloader.ViewModels;
using MahApps.Metro.Controls;

namespace FileDownloader.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : MetroWindow
{
    public MainWindow()
    {
        InitializeComponent();
        // Раскомментируем установку DataContext
        this.DataContext = new DownloaderViewModel(); 
    }
} 