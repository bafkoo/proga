using System.Windows.Input;
using FileDownloader.Infrastructure; // Обновляем using
using FileDownloader.Models; // Обновляем using
using System.Windows; // Для MessageBox
using CommunityToolkit.Mvvm.ComponentModel; // Добавляем using для ObservableObject
using CommunityToolkit.Mvvm.Input; // Добавляем using для RelayCommand

namespace FileDownloader.ViewModels; // Обновляем пространство имен

public class SettingsViewModel : ObservableObject
{
    // Ссылка на оригинальные настройки (или сервис настроек)
    private readonly ApplicationSettings _originalSettings;

    // Редактируемая копия настроек
    private ApplicationSettings _editableSettings;
    public ApplicationSettings EditableSettings
    {
        get => _editableSettings;
        // Обычно ViewModel не имеет публичного сеттера для своей основной модели
        // Обновления идут через свойства внутри EditableSettings
        private set => SetProperty(ref _editableSettings, value);
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    // Делегат для сообщения о сохранении
    public Action<ApplicationSettings> OnSave { get; set; }
    // Делегат для закрытия окна
    public Action CloseWindow { get; set; }

    // Конструктор принимает текущие настройки
    public SettingsViewModel(ApplicationSettings currentSettings)
    {
        _originalSettings = currentSettings; // Сохраняем ссылку на оригинал
        // Создаем глубокую копию для редактирования
        EditableSettings = new ApplicationSettings(currentSettings);

        SaveCommand = new RelayCommand(SaveChanges);
        CancelCommand = new RelayCommand(CancelChanges);
    }

    private void SaveChanges()
    {
        // TODO: Добавить валидацию перед сохранением
        // Например, проверить, что порты - положительные числа, хост не пустой и т.д.
        if (EditableSettings.MaxParallelDownloads <= 0)
        {
             MessageBox.Show("Максимальное число потоков должно быть больше нуля.", "Ошибка валидации", MessageBoxButton.OK, MessageBoxImage.Warning);
             return;
        }
         if (EditableSettings.SleepIntervalMilliseconds < 0)
        {
             MessageBox.Show("Интервал паузы не может быть отрицательным.", "Ошибка валидации", MessageBoxButton.OK, MessageBoxImage.Warning);
             return;
        }
        
        // Здесь мы не обновляем оригинальный объект напрямую,
        // а сообщаем главному ViewModel, что нужно применить новые настройки
        OnSave?.Invoke(EditableSettings);
        CloseWindow?.Invoke();
    }

    private void CancelChanges()
    {
        // Просто закрываем окно, никаких изменений не применяем
        CloseWindow?.Invoke();
    }
} 