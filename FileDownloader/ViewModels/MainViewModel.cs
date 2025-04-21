using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileDownloader.Infrastructure.Services;
using FileDownloader.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using System.Windows; // For MessageBox

namespace FileDownloader.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IDatabaseService _databaseService;
    private readonly ILogger<MainViewModel> _logger;

    // Constructor Injection
    public MainViewModel(IDatabaseService databaseService, ILogger<MainViewModel> logger)
    {
        _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [RelayCommand]
    private async Task TestInsertAsync()
    {
        _logger.LogInformation("Запуск тестовой вставки...");

        // --- Создание тестовых данных ---
        var testAttachment = new AttachmentModel
        {
            DbID = 99, // Тестовое значение
            N = 1,     // Тестовое значение
            FileName = $"TestFile_{DateTime.Now:yyyyMMddHHmmss}.txt",
            Url = "ftp://test.com/testfile.txt",
            FileSize = "1234",
            DocDescription = "Это тестовый файл, вставленный из приложения",
            DocDate = DateTime.UtcNow,
            // Устанавливаем null для необязательных внешних ключей для простоты теста
            AttachmentsId = null,
            MedicalCommissionDecisionId = null,
            PublishedContentId = $"TestPubContent_{Guid.NewGuid()}",
            ContentId = $"TestContent_{Guid.NewGuid()}",
            Content = "Некоторое содержимое (если применимо)",
            NotificationAttachmentsId = null,
            AttachmentId = null, // Это поле кажется избыточным, если есть AttachmentsId
            UnableProvideContractGuaranteeDocsId = null
        };

        try
        {
            string? errorMessage = await _databaseService.InsertAttachmentAsync(testAttachment);

            if (errorMessage == null)
            {
                _logger.LogInformation("Тестовая вставка прошла успешно для файла {FileName}.", testAttachment.FileName);
                MessageBox.Show("Тестовая вставка прошла успешно!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                _logger.LogWarning("Процедура вставки вернула ошибку: {Error}", errorMessage);
                MessageBox.Show($"Ошибка от хранимой процедуры: {errorMessage}", "Ошибка процедуры", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критическая ошибка во время тестовой вставки");
            MessageBox.Show($"Произошла критическая ошибка: {ex.Message}", "Критическая ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Другие свойства и команды ViewModel...
} 