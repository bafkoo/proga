using System.Windows;
using FileDownloader.ViewModels;
using FileDownloader.Models;
using MahApps.Metro.Controls;

namespace FileDownloader.Views;

/// <summary>
/// Логика взаимодействия для SettingsWindow.xaml
/// </summary>
public partial class SettingsWindow : MetroWindow
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Подписываемся на запрос закрытия от ViewModel
        viewModel.CloseWindow += Close; 
        // Примечание: это создает утечку памяти, если ViewModel живет дольше окна.
        // В более сложных сценариях лучше использовать Messenger или другие механизмы.
        // Для простого модального окна это приемлемо.
    }
} 