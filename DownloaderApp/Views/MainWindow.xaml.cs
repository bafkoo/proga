using System.Windows;
using DownloaderApp.ViewModels;
using MahApps.Metro.Controls;

namespace DownloaderApp.Views
{
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
} 