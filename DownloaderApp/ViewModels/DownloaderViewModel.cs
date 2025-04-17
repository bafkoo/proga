namespace FileDownloader.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
// using FileDownloader.Infrastructure;
using DownloaderApp.Infrastructure; // Corrected namespace for ArchiveService
using Microsoft.Extensions.Configuration;
using FluentFTP;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using FileDownloader.Models;
using FileDownloader.Views;
using System.Diagnostics;
using System.Linq;
using FluentFTP.Exceptions;
using ControlzEx.Theming;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using static System.Windows.Clipboard;
using Serilog;
using Serilog.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite; // Добавлено для Sqlite
using SharpCompress.Archives; // <-- Добавлено
using SharpCompress.Common; // <-- Добавлено
using System.IO.Compression; // <-- Для Zip (если понадобится отдельно)
using System.Collections.Concurrent; // Для потокобезопасной коллекции
using System.Text.RegularExpressions;
using System.Windows.Threading; // Для DispatcherTimer
using System.Data; // Added for DataTable and DataRow

// --- Добавляем класс для хранения статистики по датам ---
public class DailyFileCount : INotifyPropertyChanged // Реализуем интерфейс
{
    private int _processedCount;
    private int _count;
    private DateTime _date;

    public DateTime Date
    {
        get => _date;
        set => SetProperty(ref _date, value);
    }

    public int Count // Общее количество файлов на дату
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    public int ProcessedCount // Количество обработанных файлов на дату
    {
        get => _processedCount;
        set => SetProperty(ref _processedCount, value);
    }

    // --- Реализация INotifyPropertyChanged ---
    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
    // --- Конец реализации INotifyPropertyChanged ---
}
// --- Конец класса DailyFileCount ---

// --- Добавляем класс DownloadResult ---
public class DownloadResult
{
    public bool Success { get; set; }
    public long? ExpectedSize { get; set; } // Ожидаемый размер из Content-Length (может быть null)
    public long ActualSize { get; set; }     // Фактический размер скачанного файла
    public HttpStatusCode? StatusCode { get; set; } // HTTP статус ответа
    public string ErrorMessage { get; set; } // Сообщение об ошибке
    public string TempFilePath { get; set; } // Путь к временному файлу (если он остался)
    public TimeSpan? RetryAfterHeaderValue { get; set; } // <-- Добавлено поле для Retry-After
}
// --- Конец класса DownloadResult ---

// --- Состояния Circuit Breaker ---
enum CircuitBreakerState { Closed, Open, HalfOpen }

// --- Вспомогательная запись для потоковой передачи данных файла ---
internal record FileMetadataRecord(
    int DocumentMetaID,
    string Url,
    DateTime PublishDate,
    string ComputerName,
    string DirectoryName,
    int DocumentMetaPathID,
    string PathDirectory, // pthDocument
    string FlDocument, // flDocumentOriginal
    string Ftp, // ftp
    string FileNameFtp, // fileNameFtp
    string FileName, // originalFileName
    string ExpName,
    string DocDescription,
    object UrlID // urlID from DB (может быть int или string)
);
// --------------------------------------------------------------

public class DownloaderViewModel : ObservableObject, IDataErrorInfo
{
    // --- Circuit Breaker State ---
    private volatile CircuitBreakerState _breakerState = CircuitBreakerState.Closed;
    private DateTime _breakerOpenUntilUtc = DateTime.MinValue;
    private volatile int _consecutive429Failures = 0;
    private const int BreakerFailureThreshold = 5; // Порог ошибок для размыкания
    private readonly TimeSpan BreakerOpenDuration = TimeSpan.FromSeconds(30); // Время размыкания
    private object _breakerLock = new object(); // Для синхронизации перехода в HalfOpen

    // --- Генератор случайных чисел для Jitter ---
    private static readonly Random _random = new Random();

    // --- Адаптивная задержка для Rate Limiting ---
    private volatile int _adaptiveDelayMilliseconds = 0; // Используем volatile для потокобезопасного чтения/записи

    // --- Текущие Активные Настройки ---
    private ApplicationSettings _currentSettings = new ApplicationSettings();
    public ApplicationSettings CurrentSettings
    {
        get => _currentSettings;
        private set => SetProperty(ref _currentSettings, value);
    }

    // Восстанавливаем свойство CurrentFtpSettings
    private FtpSettings _currentFtpSettings = new FtpSettings();
    public FtpSettings CurrentFtpSettings
    {
        get => _currentFtpSettings;
        private set => SetProperty(ref _currentFtpSettings, value);
    }

    // --- Параметры загрузки (Свойства, к которым будет привязан UI) ---

    private DatabaseInfo _selectedDatabase;
    public DatabaseInfo SelectedDatabase
    {
        get => _selectedDatabase;
        set
        {
            if (SetProperty(ref _selectedDatabase, value))
            {
                // Загружаем темы при смене базы данных, но используем _iacConnectionString
                LoadAvailableThemes(); 
                // Обновляем состояние команды StartDownloadCommand, так как изменилась зависимость
                 if (StartDownloadCommand is RelayCommand rc) rc.NotifyCanExecuteChanged();
            }
        }
    }


    private ThemeInfo _selectedTheme;
    public ThemeInfo SelectedTheme
    {
        get => _selectedTheme;
        set 
        {
            if (SetProperty(ref _selectedTheme, value))
            {
                 // Force re-evaluation of the StartDownloadCommand's CanExecute when the theme changes
                 if (StartDownloadCommand is RelayCommand rc) rc.NotifyCanExecuteChanged();
            }
        }
    }

    // --- НОВЫЕ Свойства для UI Тем MahApps ---
    public ObservableCollection<string> AvailableBaseUiThemes { get; } = new ObservableCollection<string>();
    public ObservableCollection<string> AvailableAccentUiColors { get; } = new ObservableCollection<string>();

    private string _selectedBaseUiTheme;
    public string SelectedBaseUiTheme
    {
        get => _selectedBaseUiTheme;
        set
        {
            if (SetProperty(ref _selectedBaseUiTheme, value))
            {
                ApplyUiTheme();
            }
        }
    }

    private string _selectedAccentUiColor;
    public string SelectedAccentUiColor
    {
        get => _selectedAccentUiColor;
        set
        {
            if (SetProperty(ref _selectedAccentUiColor, value))
            {
                ApplyUiTheme();
            }
        }
    }
    // --- Конец новых свойств для UI Тем ---

    private DateTime _beginDate = DateTime.Today.AddDays(-7);
    public DateTime BeginDate
    {
        get => _beginDate;
        set => SetProperty(ref _beginDate, value);
    }

    private DateTime _endDate = DateTime.Today;
    public DateTime EndDate
    {
        get => _endDate;
        set => SetProperty(ref _endDate, value);
    }

    private int? _selectedThemeId; // Nullable, если тема может быть не выбрана
    public int? SelectedThemeId
    {
        get => _selectedThemeId;
        set => SetProperty(ref _selectedThemeId, value);
    }

    private int _selectedSourceId = 0; // 0, 1 или 2
    public int SelectedSourceId
    {
        get => _selectedSourceId;
        set => SetProperty(ref _selectedSourceId, value);
    }

    private int _selectedFilterId = 0; // filterIDdocumentMeta
    public int SelectedFilterId
    {
        get => _selectedFilterId;
        set => SetProperty(ref _selectedFilterId, value);
    }

    private bool _checkProvError;
    public bool CheckProvError // Вместо chkProvError.Checked
    {
        get => _checkProvError;
        set => SetProperty(ref _checkProvError, value);
    }

     private bool _ignoreDownloadErrors; // Вместо chkError.Checked
    public bool IgnoreDownloadErrors
    {
        get => _ignoreDownloadErrors;
        set => SetProperty(ref _ignoreDownloadErrors, value);
    }

    // SleepIntervalMilliseconds теперь в CurrentSettings

    // --- Состояние процесса (Для отображения прогресса и статуса) ---

    private int _totalFiles;
    public int TotalFiles
    {
        get => _totalFiles;
        private set => SetProperty(ref _totalFiles, value);
    }

    private int _processedFiles;
    public int ProcessedFiles
    {
        get => _processedFiles;
        private set => SetProperty(ref _processedFiles, value);
    }

    private string _currentFileName = "";
    public string CurrentFileName
    {
        get => _currentFileName;
        private set => SetProperty(ref _currentFileName, value);
    }

    private string _statusMessage = "Готов";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set => SetProperty(ref _isDownloading, value);
    }

    // --- Свойства для лога ---
    private ObservableCollection<LogMessage> _logMessages = new ObservableCollection<LogMessage>();
    public ObservableCollection<LogMessage> LogMessages
    {
        get => _logMessages;
        private set => SetProperty(ref _logMessages, value);
    }

    private ObservableCollection<LogMessage> _filteredLogMessages = new ObservableCollection<LogMessage>();
    public ObservableCollection<LogMessage> FilteredLogMessages
    {
        get => _filteredLogMessages;
        private set => SetProperty(ref _filteredLogMessages, value);
    }

    private ObservableCollection<LogFilterType> _logFilterTypes = new ObservableCollection<LogFilterType>();
    public ObservableCollection<LogFilterType> LogFilterTypes
    {
        get => _logFilterTypes;
        private set => SetProperty(ref _logFilterTypes, value);
    }

    private LogFilterType _selectedLogFilterType;
    public LogFilterType SelectedLogFilterType
    {
        get => _selectedLogFilterType;
        set
        {
            if (SetProperty(ref _selectedLogFilterType, value))
            {
                UpdateFilteredLogMessages();
            }
        }
    }

    // --- Команды (Для кнопок) ---
    public ICommand StartDownloadCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand CopyLogToClipboardCommand { get; }
    public ICommand OpenLogDirectoryCommand { get; }

    // --- Новая коллекция для статистики по датам ---
    public ObservableCollection<DailyFileCount> FileCountsPerDate { get; } = new ObservableCollection<DailyFileCount>();

    // --- CancellationTokenSource ---
    private CancellationTokenSource _cancellationTokenSource;

    // --- Свойства конфигурации --- 
    private string _baseConnectionString;
    private string _iacConnectionString;
    private string _serverOfficeConnectionString;

    private static readonly HttpClient _httpClient;

    private bool _updateFlag;
    public bool UpdateFlag
    {
        get => _updateFlag;
        set
        {
            if (_updateFlag != value)
            {
                _updateFlag = value;
                OnPropertyChanged();
            }
        }
    }

    // Добавляем новые свойства для улучшения UI
    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        set
        {
            if (_downloadProgress != value)
            {
                _downloadProgress = value;
                OnPropertyChanged();
            }
        }
    }

    private string _currentFileDetails;
    public string CurrentFileDetails
    {
        get => _currentFileDetails;
        set
        {
            if (_currentFileDetails != value)
            {
                _currentFileDetails = value;
                OnPropertyChanged();
            }
        }
    }

    private string _downloadSpeed;
    public string DownloadSpeed
    {
        get => _downloadSpeed;
        set
        {
            if (_downloadSpeed != value)
            {
                _downloadSpeed = value;
                OnPropertyChanged();
            }
        }
    }

    private string _estimatedTimeRemaining;
    public string EstimatedTimeRemaining
    {
        get => _estimatedTimeRemaining;
        set
        {
            if (_estimatedTimeRemaining != value)
            {
                _estimatedTimeRemaining = value;
                OnPropertyChanged();
            }
        }
    }

    private DateTime _downloadStartTime;
    private long _totalBytesDownloaded;
    private long _lastBytesDownloaded;
    private DateTime _lastProgressUpdate;

    // --- Поля для пакетного обновления UI ---
    private DispatcherTimer _uiUpdateTimer;
    private ConcurrentQueue<DateTime> _processedDatesSinceLastUpdate = new ConcurrentQueue<DateTime>();
    private long _lastProcessedCountForUI = 0; 
    private const int UIUpdateIntervalMilliseconds = 1000; 
    private long _processedFilesCounter = 0; // <--- Делаем полем класса
    // --------------------------------------

    // --- Очередь для буферизации логов ---
    private ConcurrentQueue<LogMessage> _logMessageQueue = new ConcurrentQueue<LogMessage>();

    // --- Поле для хранения имени последнего обрабатываемого файла ---
    private string _lastProcessedFileName = null;

    // --- Словарь для быстрой статистики по датам ---
    private Dictionary<DateTime, DailyFileCount> _fileCountsDict = new Dictionary<DateTime, DailyFileCount>();

    // --- Флаг завершения асинхронной инициализации ---
    private volatile bool _isInitialized = false;

    private ArchiveService _archiveServiceForExtractedFiles; // Instance for handling extracted files

    static DownloaderViewModel()
    {
        // Настройка обработчика HTTP
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        // Устанавливаем таймаут в 2 минуты
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };

        // Настройка заголовков по умолчанию
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
        _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
        _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
        _httpClient.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
        _httpClient.DefaultRequestHeaders.Add("Pragma", "no-cache");
        _httpClient.DefaultRequestHeaders.Add("Sec-Ch-Ua", "\"Chromium\";v=\"122\", \"Not(A:Brand\";v=\"24\", \"Google Chrome\";v=\"122\"");
        _httpClient.DefaultRequestHeaders.Add("Sec-Ch-Ua-Mobile", "?0");
        _httpClient.DefaultRequestHeaders.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
        _httpClient.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
        _httpClient.DefaultRequestHeaders.Add("DNT", "1");
    }

    // --- Конструктор ---
    public DownloaderViewModel()
    {
        FileLogger.Log("Конструктор DownloaderViewModel: Начало");
        
        LoadConfigurationAndSettings(); 
        FileLogger.Log("Конструктор DownloaderViewModel: LoadConfigurationAndSettings завершен");

        // Убираем синхронные вызовы LoadAvailableDatabases и LoadAvailableThemes отсюда
        // LoadAvailableDatabases();
        // LoadAvailableThemes(); 

        LoadUiThemesAndAccents();
        FileLogger.Log("Конструктор DownloaderViewModel: LoadUiThemesAndAccents завершен");

        // Инициализация команд (обновляем CanExecute для StartDownloadCommand)
        StartDownloadCommand = new RelayCommand(
            async () => await StartDownloadAsync(),
            // Теперь проверяем IsInitialized
            () => !IsDownloading && _isInitialized && !string.IsNullOrEmpty(_baseConnectionString) && SelectedDatabase != null && SelectedTheme != null // Убрали проверку Error
        );
        CancelDownloadCommand = new RelayCommand(
            () => CancelDownload(),
            () => IsDownloading
        );
        OpenSettingsCommand = new RelayCommand(OpenSettingsWindow, () => !IsDownloading);
        ClearLogCommand = new RelayCommand(
            () => { LogMessages.Clear(); UpdateFilteredLogMessages(); }, // Добавили обновление фильтра
            () => LogMessages.Count > 0
        );
        CopyLogToClipboardCommand = new RelayCommand(
            () => SetText(string.Join(Environment.NewLine, FilteredLogMessages.Select(lm => $"[{lm.Timestamp:G}] [{lm.Type}] {lm.Message}"))), // Копируем отфильтрованные
            () => FilteredLogMessages.Count > 0
        );
        OpenLogDirectoryCommand = new RelayCommand<LogMessage>(OpenLogDirectory, CanOpenLogDirectory);
        FileLogger.Log("Конструктор DownloaderViewModel: Команды инициализированы");

        StatusMessage = "Инициализация..."; // Начальный статус изменен
        FileLogger.Log("Конструктор DownloaderViewModel: Завершен");

        InitializeUiUpdateTimer();

        // Запускаем асинхронную инициализацию
        _ = InitializeAsync(); // Вызов без await
    }

    private async Task InitializeAsync()
    {
        AddLogMessage("Начало асинхронной инициализации...");
        try
        {
            // Запускаем загрузку параллельно
            Task dbLoadTask = LoadAvailableDatabasesAsync();
            Task themeLoadTask = LoadAvailableThemesAsync();

            // Ожидаем завершения обеих задач
            await Task.WhenAll(dbLoadTask, themeLoadTask);

            _isInitialized = true; // Устанавливаем флаг
            StatusMessage = "Готов"; // Обновляем статус
            AddLogMessage("Асинхронная инициализация успешно завершена.", "Success");

             // Уведомляем команду, что она может стать активной
            Application.Current?.Dispatcher?.Invoke(() => 
            {
                 (StartDownloadCommand as RelayCommand)?.NotifyCanExecuteChanged();
            });
        }
        catch (Exception ex)
        {
            _isInitialized = false; // Остаемся неинициализированными
            StatusMessage = "Ошибка инициализации";
            AddLogMessage($"Критическая ошибка во время асинхронной инициализации: {ex.Message}", "Error");
            FileLogger.Log($"InitializeAsync Exception: {ex}");
            // Можно показать MessageBox или другой индикатор критической ошибки
        }
    }

    private void InitializeUiUpdateTimer()
    {
        _uiUpdateTimer = new DispatcherTimer(DispatcherPriority.Background); // Низкий приоритет
        _uiUpdateTimer.Interval = TimeSpan.FromMilliseconds(UIUpdateIntervalMilliseconds);
        _uiUpdateTimer.Tick += UiUpdateTimer_Tick;
    }

    private async Task StartDownloadAsync()
    {
        // --- Инициализация перед началом ---
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        IsDownloading = true;
        (StartDownloadCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (CancelDownloadCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (OpenSettingsCommand as RelayCommand)?.NotifyCanExecuteChanged(); // Блокируем настройки во время загрузки

        // Отслеживание уже обработанных файлов в ЭТОМ сеансе
        var processedFileIdsInThisSession = new ConcurrentDictionary<int, bool>();

        // Переменные для статистики и статуса
        string finalStatus = "Загрузка завершена.";
        string basePath = null; // Переменная для хранения базового пути
        StatusMessage = "Инициализация...";
        LogMessages.Clear();
        FileCountsPerDate.Clear();
        _fileCountsDict.Clear(); // Очищаем словарь тоже
        AddLogMessage("Запуск процесса загрузки...");

        // Получаем параметры из UI один раз
        string databaseName = SelectedDatabase.Name;
        int themeId = SelectedTheme.Id;
        int srcID = SelectedSourceId;
        bool flProv = CheckProvError;
        DateTime dtB = BeginDate.Date; // Убедимся, что время 00:00:00
        DateTime dtE = EndDate.Date.AddDays(1).AddTicks(-1); // Включаем весь конечный день до 23:59:59.9999999
        int filterId = SelectedFilterId;

        var semaphore = new SemaphoreSlim(CurrentSettings.MaxParallelDownloads);
        var conStrBuilder = new SqlConnectionStringBuilder(_baseConnectionString) { InitialCatalog = databaseName };
        string targetDbConnectionString = conStrBuilder.ConnectionString;
        string iacConnectionString = _iacConnectionString; // Предполагаем, что это тоже нужно передавать?

        TimeSpan checkInterval = TimeSpan.FromMinutes(30); // Интервал между проверками новых файлов
        bool firstCheck = true; // Флаг для первой проверки

        // --- Сброс перед стартом таймера и пакетного обновления ---
        _processedFilesCounter = 0; // <--- Инициализируем поле класса
        _processedDatesSinceLastUpdate = new ConcurrentQueue<DateTime>(); 
        _lastProcessedCountForUI = 0; 
        ProcessedFiles = 0; // Сброс UI счетчика в начале
        _uiUpdateTimer.Start(); // Запускаем таймер обновления UI
        // ------------------------------------------------------

        try
        {
            // --- Основной цикл мониторинга ---
            while (DateTime.Now <= dtE && !token.IsCancellationRequested)
            {
                if (!firstCheck)
                {
                    AddLogMessage($"Ожидание {checkInterval.TotalMinutes} минут перед следующей проверкой новых файлов...");
                    await Task.Delay(checkInterval, token); // Пауза перед следующей проверкой, прерываемая отменой
                }
                firstCheck = false;

                if (token.IsCancellationRequested) break; // Проверка отмены после паузы

                AddLogMessage($"{(processedFileIdsInThisSession.IsEmpty ? "Первичная" : "Повторная")} проверка файлов за период с {dtB:dd.MM.yyyy} по {dtE:dd.MM.yyyy HH:mm:ss}...");
                StatusMessage = "Получение списка файлов...";

                DataTable dtTab = null;
                int currentTotalFiles = 0;

                // --- Получение списка файлов ---
                try
                {
                    using (SqlConnection conBase = new SqlConnection(targetDbConnectionString))
                    {
                        await conBase.OpenAsync(token);
                        dtTab = await FetchFileListAsync(conBase, dtB, dtE, themeId, srcID, filterId, flProv, token);
                        currentTotalFiles = dtTab?.Rows.Count ?? 0;

                        // Инициализация TotalFiles и FileCountsPerDate при первой проверке или при появлении новых файлов
                        if (firstCheck && currentTotalFiles > 0) 
                        {
                             TotalFiles = currentTotalFiles; 
                             AddLogMessage($"Обнаружено {TotalFiles} файлов для обработки за период.");
                             // Определение базового пути (делаем один раз, если возможно)
                             if (basePath == null && srcID == 0 && dtTab.Rows.Count > 0)
                             {
                                 try { /* ... код определения basePath ... */ }
                                 catch (Exception pathEx) { AddLogMessage($"Ошибка при определении базового пути: {pathEx.Message}"); }
                             }
                             // Асинхронная Инициализация статистики
                             await InitializeDateStatisticsAsync(dtTab);
                        }
                        else if (!firstCheck && currentTotalFiles > TotalFiles) 
                        {
                            AddLogMessage($"Обнаружено {currentTotalFiles - TotalFiles} новых файлов. Общее количество теперь: {currentTotalFiles}");
                            TotalFiles = currentTotalFiles; // Обновляем общее количество
                            // Асинхронное Обновление статистики
                            await UpdateDateStatisticsAsync(dtTab);
                        }
                        else if (firstCheck && currentTotalFiles == 0)
                        {
                            TotalFiles = 0;
                            ProcessedFiles = 0;
                            FileCountsPerDate.Clear(); // Убедимся, что статистика пуста
                            _fileCountsDict.Clear(); // Очищаем словарь
                            AddLogMessage("Не найдено файлов для обработки за указанный период.");
                        }
                    }
                }
                catch (OperationCanceledException) { throw; } // Переброс отмены
                catch (Exception ex)
                {
                    AddLogMessage($"Критическая ошибка при получении списка файлов: {ex.Message}. Проверка будет повторена через {checkInterval.TotalMinutes} минут.", "Error");
                    StatusMessage = "Ошибка получения списка файлов.";
                    // Не выходим из цикла, ждем следующей попытки
                    continue; // Переходим к следующей итерации внешнего цикла (после паузы)
                }

                if (dtTab == null || dtTab.Rows.Count == 0)
                {
                    AddLogMessage("Не найдено файлов для обработки в указанном диапазоне или произошла ошибка при получении списка.");
                    // Если файлов нет, просто ждем следующей проверки
                    continue; // Переходим к следующей итерации внешнего цикла
                }

                // --- Фильтрация уже обработанных файлов ---
                var filesToProcess = dtTab.AsEnumerable()
                                        .Where(row => !processedFileIdsInThisSession.ContainsKey(Convert.ToInt32(row["documentMetaID"])))
                                        .ToList(); // Создаем список строк для обработки в этой итерации

                if (!filesToProcess.Any())
                {
                    AddLogMessage("Новых файлов для обработки не найдено в этой проверке.");
                    continue; // Переходим к следующей итерации внешнего цикла
                }

                AddLogMessage($"Найдено {filesToProcess.Count} новых файлов для обработки в этой проверке.");
                StatusMessage = $"Обработка {filesToProcess.Count} новых файлов...";

                // --- Обработка новых файлов ---
                var tasks = new List<Task>();
                var progressReporter = new Progress<double>(progress => DownloadProgress = progress);

                foreach (DataRow row in filesToProcess)
                {
                    if (token.IsCancellationRequested) break;

                    await semaphore.WaitAsync(token); // Ожидаем свободный слот

                    tasks.Add(Task.Run(async () =>
                    {
                        int documentMetaId = Convert.ToInt32(row["documentMetaID"]);
                        DateTime publishDate = DateTime.Now; // Инициализация значением по умолчанию
                        try
                        {
                            if (token.IsCancellationRequested) return;

                            publishDate = DateTime.Parse(row["publishDate"].ToString()).Date;

                            // Обрабатываем файл
                            await ProcessFileAsync(row, targetDbConnectionString, iacConnectionString, databaseName, srcID, flProv, themeId, token, progressReporter);

                            // Отмечаем как успешно обработанный в этом сеансе
                            processedFileIdsInThisSession.TryAdd(documentMetaId, true);

                            // --- ЛОГИКА ДЛЯ ПАКЕТНОГО ОБНОВЛЕНИЯ ---                            
                            Interlocked.Increment(ref _processedFilesCounter); // <--- Используем поле класса
                            _processedDatesSinceLastUpdate.Enqueue(publishDate);
                            // --- КОНЕЦ ЛОГИКИ ДЛЯ ПАКЕТНОГО ОБНОВЛЕНИЯ ---

                            // УБИРАЕМ ПРЯМОЕ ОБНОВЛЕНИЕ UI ОТСЮДА
                            /*
                            Application.Current.Dispatcher.Invoke(() => {
                                ProcessedFiles = (int)processedFilesCounter; // Обновляем UI
                                var dailyStat = FileCountsPerDate.FirstOrDefault(d => d.Date == publishDate);
                                if (dailyStat != null) { dailyStat.ProcessedCount++; }
                                // Логика добавления новой даты, если ее нет, должна быть в Initialize/UpdateDateStatistics
                            });
                            */
                        }
                        catch (OperationCanceledException)
                        {
                             AddLogMessage($"Обработка файла (ID: {documentMetaId}) отменена.", "Warning");
                             // Не добавляем в processedFileIdsInThisSession при отмене
                        }
                        catch (Exception ex)
                        {
                            string originalFileName = row.Table.Columns.Contains("fileName") ? row["fileName"].ToString() : $"ID: {documentMetaId}";
                            AddLogMessage($"Ошибка при обработке файла '{originalFileName}': {ex.Message}", "Error");
                            if (!IgnoreDownloadErrors)
                            {
                                // Если не игнорируем ошибки, можно решить прервать ли весь процесс
                                // throw; // Переброс ошибки прервет Task.WhenAll
                                AddLogMessage($"Обработка файла '{originalFileName}' пропущена из-за ошибки.", "Warning");
                            }
                            else
                            {
                                AddLogMessage($"Ошибка обработки файла '{originalFileName}' проигнорирована согласно настройкам.", "Warning");
                                // Можно добавить ID в processedFileIdsInThisSession, чтобы не пытаться обработать его снова
                                // processedFileIdsInThisSession.TryAdd(documentMetaId, false); // false - признак ошибки? или просто не добавлять? Пока не добавляем.
                            }
                        }
                        finally
                        {
                            semaphore.Release(); // Освобождаем слот
                        }
                    }, token));
                }

                // Ожидаем завершения всех задач этой пачки
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException)
                {
                     AddLogMessage("Операция отменена пользователем во время обработки файлов.", "Warning");
                     throw; // Перебрасываем для выхода из внешнего цикла
                }
                // Другие исключения (если ProcessFileAsync их перебрасывает и IgnoreDownloadErrors = false) будут обработаны внешним catch

                AddLogMessage("Обработка текущей пачки новых файлов завершена.");

            } // --- Конец основного цикла мониторинга ---

            // Формируем итоговый статус после завершения цикла
            if (token.IsCancellationRequested)
            {
                finalStatus = "Загрузка отменена пользователем.";
            }
            else if (DateTime.Now > dtE)
            {
                finalStatus = $"Мониторинг завершен. Достигнута конечная дата: {dtE:dd.MM.yyyy HH:mm:ss}.";
                AddLogMessage(finalStatus);
            } else {
                // Сюда не должны попасть при нормальном завершении цикла while, но на всякий случай
                finalStatus = "Загрузка завершена.";
            }
             AddLogMessage($"Всего обработано файлов в этом сеансе: {_processedFilesCounter}");

        }
        catch (OperationCanceledException)
        {
            finalStatus = "Загрузка отменена.";
            AddLogMessage(finalStatus, "Warning");
        }
        catch (Exception ex)
        {
            finalStatus = $"Ошибка во время загрузки: {ex.Message}";
            AddLogMessage($"Критическая ошибка: {ex.ToString()}", "Error"); // Логируем полный стектрейс
            // Показываем ошибку пользователю
            // MessageBox.Show($"Произошла критическая ошибка во время загрузки:\n\n{ex.Message}\n\nПодробности смотрите в логах.", 
            //                 "Ошибка загрузки", 
            //                 MessageBoxButton.OK, 
            //                 MessageBoxImage.Error);
        }
        finally
        {
            _uiUpdateTimer.Stop(); // Останавливаем таймер
            // Выполняем финальное обновление UI, чтобы учесть все, что накопилось
            UpdateUiFromTimerTick(); 
            
            // --- Завершение и очистка ---
            IsDownloading = false;
            // Используем поле класса _processedFilesCounter
            if (_processedFilesCounter > 0 && !string.IsNullOrEmpty(basePath))
            {
                finalStatus += $" Файлы сохранены в: {basePath}..."; // Убрать одинарный обратный слеш
                AddLogMessage($"Успешно обработанные файлы сохранены в подпапки директории: {basePath}");
            }
            // Используем поле класса _processedFilesCounter
            else if (_processedFilesCounter > 0 && srcID != 0)
            {
                 AddLogMessage($"Обработка файлов для источника ID={srcID} завершена.");
            }

            StatusMessage = finalStatus;
            CurrentFileName = ""; // Очищаем имя текущего файла
            DownloadProgress = 0; // Сбрасываем прогресс бар
            DownloadSpeed = ""; // Очищаем скорость
            EstimatedTimeRemaining = ""; // Очищаем время

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            (StartDownloadCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (CancelDownloadCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (OpenSettingsCommand as RelayCommand)?.NotifyCanExecuteChanged(); // Разблокируем настройки

            // В блоке finally обновляем статус загрузки - UpdateFlag не используется в этой логике, убираем
            // UpdateFlag = false;
        }
    }

    private void UiUpdateTimer_Tick(object sender, EventArgs e)
    {
        // Вызываем обновление UI из таймера в UI потоке
        Application.Current?.Dispatcher?.BeginInvoke(
            new Action(() => UpdateUiFromTimerTick()),
            DispatcherPriority.Background // Используем низкий приоритет
        );
    }

    private void UpdateUiFromTimerTick()
    {
        // Обновление счетчиков
        long currentTotalProcessed = Interlocked.Read(ref _processedFilesCounter);
        // Обновляем главный счетчик, только если значение изменилось
        if (currentTotalProcessed != _lastProcessedCountForUI)
        {
            ProcessedFiles = (int)currentTotalProcessed;
            _lastProcessedCountForUI = currentTotalProcessed;
        }

        // Обработка очереди дат (ОПТИМИЗИРОВАНО)
        var datesToUpdate = new Dictionary<DateTime, int>();
        while (_processedDatesSinceLastUpdate.TryDequeue(out DateTime date))
        {
            if (datesToUpdate.ContainsKey(date))
                datesToUpdate[date]++;
            else
                datesToUpdate[date] = 1;
        }

        if (datesToUpdate.Count > 0)
        {
            // Обновляем статистику в UI потоке, используя словарь
            foreach (var kvp in datesToUpdate)
            {
                // Быстрый поиск в словаре
                if (_fileCountsDict.TryGetValue(kvp.Key, out var dailyStat))
                {
                    dailyStat.ProcessedCount += kvp.Value;
                }
                else
                {
                    // Словарь должен быть синхронизирован с ObservableCollection
                    // Эта ветка маловероятна при правильной инициализации/обновлении
                    AddLogMessage($"UpdateUiFromTimerTick: Не найдена статистика в словаре для даты {kvp.Key:dd.MM.yyyy}.", "Warning");
                    // Попытка найти в ObservableCollection (медленнее)
                    var statFromList = FileCountsPerDate.FirstOrDefault(d => d.Date == kvp.Key);
                    if (statFromList != null)
                        statFromList.ProcessedCount += kvp.Value;
                }
            }
        }

        // --- Обновление имени текущего файла ---
        string latestFileName = _lastProcessedFileName; // Считываем последнее имя
        if (latestFileName != null && latestFileName != CurrentFileName) // Обновляем, если изменилось
        {
            CurrentFileName = latestFileName; // Присваивание вызовет OnPropertyChanged
        }
        // --- Конец обновления имени файла ---

        // --- Обработка очереди логов ---
        var logsToAdd = new List<LogMessage>();
        while (_logMessageQueue.TryDequeue(out var logMessage))
        {
            logsToAdd.Add(logMessage);
        }

        if (logsToAdd.Count > 0)
        {
            // Ограничиваем общее количество логов
            int removeCount = LogMessages.Count + logsToAdd.Count - 1000; // Макс. 1000 логов
            if (removeCount > 0)
            {
                for (int i = 0; i < removeCount; i++)
                {
                    LogMessages.RemoveAt(0);
                }
            }

            // Добавляем новые логи пачкой
            foreach (var log in logsToAdd)
            {
                LogMessages.Add(log);
            }

            // Обновляем отфильтрованный список ОДИН РАЗ после добавления пачки
            UpdateFilteredLogMessages();
        }
        // --- Конец обработки очереди логов ---
    }

    // Вспомогательный асинхронный метод для получения списка файлов
    private async Task<DataTable> FetchFileListAsync(SqlConnection conBase, DateTime dtB, DateTime dtE, int themeId, int srcId, int filterId, bool flProv, CancellationToken token)
    {
        using (SqlCommand cmdList = new SqlCommand("documentMetaDownloadList", conBase) { CommandType = CommandType.StoredProcedure, CommandTimeout = 600 })
        {
            cmdList.Parameters.AddWithValue("@dtB", dtB);
            cmdList.Parameters.AddWithValue("@dtE", dtE);
            cmdList.Parameters.AddWithValue("@themeID", themeId);
            cmdList.Parameters.AddWithValue("@srcID", srcId);
            cmdList.Parameters.AddWithValue("@filterID", filterId);
            cmdList.Parameters.AddWithValue("@flProv", flProv);
            cmdList.Parameters.AddWithValue("@usrID", CurrentSettings.UserId);
            using (SqlDataReader reader = await cmdList.ExecuteReaderAsync(token))
            {
                DataTable dtTab = new DataTable();
                dtTab.Load(reader);
                return dtTab;
            }
        }
    }

    // Метод для обработки одного файла
    // Принимает connectionData как dynamic. В реальном приложении лучше создать класс/структуру для этого.
    private async Task ProcessFileAsync(DataRow row, string targetDbConnectionString, string iacConnectionString, string databaseName, int srcID, bool flProv, int themeId, CancellationToken token, IProgress<double> progress)
    {
        // --- Извлечение данных из строки ---
        string url = row["url"].ToString();
        DateTime publishDate = DateTime.Parse(row["publishDate"].ToString());
        string computerName = row["computerName"].ToString();
        string directoryName = row["directoryName"].ToString();
        int documentMetaPathID = Convert.ToInt32(row["documentMetaPathID"].ToString());
        int documentMetaID = Convert.ToInt32(row["documentMetaID"].ToString());
        string pthDocument = row["pathDirectory"].ToString();
        string flDocumentOriginal = row["flDocument"].ToString();
        string ftp = row["ftp"].ToString();
        string fileNameFtp = row["fileNameFtp"].ToString();
        string originalFileName = row["fileName"].ToString();
        string expName = row["expName"].ToString();
        string docDescription = row["docDescription"].ToString();
        object urlIdFromDb = row["urlID"];

        // --- Снижаем частоту обновления UI ---
        long currentCount = Interlocked.Increment(ref _processedFilesCounter);
        bool shouldUpdateUI = currentCount % 5 == 0; // Обновляем UI только на каждом 5-м файле

        // --- Определение суффикса и путей ---
        string suffixName = "";
        if (databaseName == "notificationEF") suffixName = "_nef";
        else if (databaseName == "notificationZK") suffixName = "_nzk";
        else if (databaseName == "notificationOK") suffixName = "_nok";

        string fileDocument = "";
        string pathDocument = "";

        // Логика определения пути (проверьте!)
        if (srcID == 2)
        {
            pathDocument = $@"\\{computerName}\{directoryName}\{themeId}\{publishDate.Year}\{publishDate.Month}\{publishDate.Day}"; // Используем @
            fileDocument = Path.Combine(pathDocument, $"{documentMetaID}{suffixName}.{expName}");
        }
        else if (srcID == 1)
        {
            // Путь для временного хранения перед FTP? Или конечный?
            pathDocument = $@"\\{computerName}\{directoryName}\{themeId}\{publishDate.Year}\{publishDate.Month}\{publishDate.Day}"; // Используем @
            fileDocument = Path.Combine(pathDocument, fileNameFtp); // Имя файла для FTP = локальное имя?
        }
        else // srcID == 0
        {
            pathDocument = $@"\\{computerName}\{directoryName}\{themeId}\{publishDate.Year}\{publishDate.Month}\{publishDate.Day}"; // Используем @
            fileDocument = Path.Combine(pathDocument, $"{documentMetaID}{suffixName}.{expName}");
        }

         // Проверка валидности пути перед использованием
         if (string.IsNullOrWhiteSpace(fileDocument))
         {
              throw new InvalidOperationException($"Не удалось сформировать путь к файлу для documentMetaID: {documentMetaID}, srcID: {srcID}");
         }
         if (srcID != 0 && string.IsNullOrWhiteSpace(pathDocument)) // Для srcID 0 pathDocument может быть пустым, если все части пути пустые
         {
              throw new InvalidOperationException($"Не удалось сформировать путь к директории для documentMetaID: {documentMetaID}, srcID: {srcID}");
         }

        // --- Основная логика в зависимости от флага flProv ---
        try // Обернем всю обработку файла в try-finally для гарантии задержки
        {
            if (flProv == false) // Режим скачивания
            {
                if (!string.IsNullOrEmpty(pathDocument))
                {
                     Directory.CreateDirectory(pathDocument);
                }
                else if(srcID == 0) // Для srcID 0 может быть только имя файла без пути
                {
                     // Если pathDocument пустой, значит, файл должен быть в текущей директории? Уточнить логику.
                     // Пока считаем, что fileDocument содержит полный путь или относительный от корня.
                     // Но CreateDirectory("") вызовет ошибку.
                     string dirOfFile = Path.GetDirectoryName(fileDocument);
                     if (!string.IsNullOrEmpty(dirOfFile)) {
                         Directory.CreateDirectory(dirOfFile);
                     } else {
                         // Если и директории нет, возможно, это корень диска? Или ошибка логики.
                         AddLogMessage($"Предупреждение: не удалось определить директорию для создания для файла {fileDocument}");
                     }
                } else {
                     throw new InvalidOperationException($"Путь к директории не определен для srcID={srcID}, file={fileDocument}");
                }


                if (File.Exists(fileDocument))
                {
                    AddLogMessage($"Удаление существующего файла: {fileDocument}");
                    File.Delete(fileDocument);
                }

                // --- Скачивание с логикой повтора ---
                // Логируем только для визуализации в UI, не для каждого файла
                if (shouldUpdateUI)
                {
                    AddLogMessage($"Скачивание: {url} -> {fileDocument}");
                }
                
                long fileSize = 0;
                DownloadResult downloadResult = null; // Объявляем здесь
                bool downloadSucceeded = false;
                const int maxRetries = 3; // Максимальное количество повторов
                int retryDelaySeconds = 1; // Начальная задержка перед повтором

                try
                {
                    // --- ПРОВЕРКА CIRCUIT BREAKER --- 
                    if (_breakerState == CircuitBreakerState.Open)
                    {
                        if (DateTime.UtcNow < _breakerOpenUntilUtc)
                        {
                            // Предохранитель разомкнут, блокируем запрос
                            throw new Exception($"Circuit Breaker is Open until {_breakerOpenUntilUtc}. Skipping download for '{originalFileName}'.");
                        }
                        else
                        {
                            // Время вышло, пытаемся перейти в HalfOpen (только один поток должен это сделать)
                            lock(_breakerLock)
                            { 
                                if (_breakerState == CircuitBreakerState.Open) // Доп. проверка внутри lock
                                { 
                                    _breakerState = CircuitBreakerState.HalfOpen;
                                     AddLogMessage("Circuit Breaker переходит в состояние Half-Open.");
                                }
                            }
                            // Если не мы перешли, а другой поток, то мы все еще в Open - выбросится исключение на след. итерации проверки
                        }
                    }
                    // Если мы в HalfOpen, разрешаем одну попытку (код ниже)
                    // Если мы в Closed, все как обычно

                    // --- УМНЫЙ ПОДХОД: Добавляем адаптивную задержку перед скачиванием ---
                    int currentAdaptiveDelay = _adaptiveDelayMilliseconds;
                    if (currentAdaptiveDelay > 0)
                    {
                        AddLogMessage($"Применяется адаптивная задержка: {currentAdaptiveDelay} мс");
                        await Task.Delay(currentAdaptiveDelay, token);
                    }

                    for (int attempt = 1; attempt <= maxRetries; attempt++)
                    {
                        token.ThrowIfCancellationRequested(); // Проверяем отмену перед каждой попыткой
                        
                        // Сокращаем количество логов
                        if (attempt > 1 || shouldUpdateUI) 
                        {
                            AddLogMessage($"Попытка скачивания #{attempt} для: {originalFileName}");
                        }
                        
                        downloadResult = await WebGetAsync(url, fileDocument, token, progress);

                        if (downloadResult.Success)
                        {
                            fileSize = downloadResult.ActualSize;
                            downloadSucceeded = true;
                            
                            // Сокращаем логирование
                            if (shouldUpdateUI)
                            {
                                AddLogMessage($"Файл '{originalFileName}' скачан успешно (попытка {attempt}), размер: {fileSize} байт.");
                            }
                            
                             // --- СБРОС СЧЕТЧИКОВ ПРИ УСПЕХЕ ---
                             Interlocked.Exchange(ref _consecutive429Failures, 0); // Сбрасываем счетчик ошибок 429

                             // Если мы были в HalfOpen и успешны, замыкаем предохранитель
                             if (_breakerState == CircuitBreakerState.HalfOpen)
                             {
                                  lock(_breakerLock)
                                  {
                                       if (_breakerState == CircuitBreakerState.HalfOpen) // Доп. проверка
                                       {
                                           _breakerState = CircuitBreakerState.Closed;
                                           AddLogMessage("Circuit Breaker ЗАМКНУТ после успешной попытки в Half-Open.");
                                       }
                                  }
                             }

                             // --- УМНЫЙ ПОДХОД: Уменьшаем адаптивную задержку при успехе ---
                             currentAdaptiveDelay = _adaptiveDelayMilliseconds; // Просто считываем текущее значение
                             if (currentAdaptiveDelay > 0)
                             {
                                 int newAdaptiveDelay = Math.Max(0, currentAdaptiveDelay - 500); // Уменьшаем на 0.5 сек, но не ниже 0
                                 Interlocked.CompareExchange(ref _adaptiveDelayMilliseconds, newAdaptiveDelay, currentAdaptiveDelay); // Атомарно устанавливаем новое значение, если оно не изменилось
                                 AddLogMessage($"Уменьшена адаптивная задержка до: {newAdaptiveDelay} мс");
                             }
                             break; // Выходим из цикла повторов при успехе
                        }
                        // --- ВОССТАНАВЛИВАЕМ БЛОК ELSE IF ---
                        else if (downloadResult.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxRetries) 
                        {
                            int delaySeconds = retryDelaySeconds;
                            // --- УМНЫЙ ПОДХОД: Проверяем Retry-After ---
                            if (downloadResult.RetryAfterHeaderValue.HasValue)
                            {
                                delaySeconds = (int)Math.Max(delaySeconds, downloadResult.RetryAfterHeaderValue.Value.TotalSeconds); // Берем максимум из нашей задержки и предложенной сервером
                                AddLogMessage($"Ошибка 429 (Too Many Requests) для '{originalFileName}'. Сервер просит подождать {downloadResult.RetryAfterHeaderValue.Value.TotalSeconds} сек. Используем {delaySeconds} сек...");
                            }
                            else
                            {
                                AddLogMessage($"Ошибка 429 (Too Many Requests) для '{originalFileName}'. Повтор через {delaySeconds} сек...");
                            }

                            // --- УМНЫЙ ПОДХОД: Увеличиваем адаптивную задержку ---
                            Interlocked.Add(ref _adaptiveDelayMilliseconds, 5000); // Атомарно добавляем 5 секунд к общей задержке
                            AddLogMessage($"Увеличена адаптивная задержка до: {_adaptiveDelayMilliseconds} мс");

                            // Добавляем Jitter к задержке
                            int jitterMilliseconds = _random.Next(100, 501); // от 0.1 до 0.5 сек
                            TimeSpan totalDelay = TimeSpan.FromSeconds(delaySeconds) + TimeSpan.FromMilliseconds(jitterMilliseconds);
                            AddLogMessage($"Добавлено дрожание (jitter): {jitterMilliseconds} мс. Общая задержка: {totalDelay.TotalSeconds:F1} сек.");

                            Interlocked.Add(ref _consecutive429Failures, 1); // Увеличиваем счетчик ошибок 429
                            AddLogMessage($"Последовательных ошибок 429: {_consecutive429Failures}");

                            // Проверяем, не пора ли разомкнуть предохранитель
                            if (_consecutive429Failures >= BreakerFailureThreshold && _breakerState != CircuitBreakerState.Open)
                            {
                                lock (_breakerLock) // Синхронизируем переход в Open
                                {
                                    if (_breakerState != CircuitBreakerState.Open) // Доп. проверка
                                    {
                                        _breakerOpenUntilUtc = DateTime.UtcNow + BreakerOpenDuration;
                                        _breakerState = CircuitBreakerState.Open;
                                        AddLogMessage($"Circuit Breaker РАЗОМКНУТ на {BreakerOpenDuration.TotalSeconds} секунд из-за {_consecutive429Failures} ошибок 429 подряд.");
                                    }
                                }
                            }

                            await Task.Delay(totalDelay, token);
                            retryDelaySeconds *= 2; // Увеличиваем *нашу* экспоненциальную задержку на случай, если Retry-After не было
                        }
                        // --- КОНЕЦ ВОССТАНОВЛЕННОГО БЛОКА ELSE IF ---
                        else // Успех или другая ошибка (или последняя попытка 429)
                        {
                            // Успех уже обработан в первом if. Здесь только ошибки.
                            AddLogMessage($"Не удалось скачать файл '{originalFileName}' после {attempt} попыток. Ошибка: {downloadResult.ErrorMessage}");
                            
                            // Если мы были в HalfOpen и получили ошибку (любую), снова размыкаем предохранитель
                            if (_breakerState == CircuitBreakerState.HalfOpen)
                            {
                                 lock(_breakerLock)
                                 {
                                     if (_breakerState == CircuitBreakerState.HalfOpen)
                                     {
                                         _breakerOpenUntilUtc = DateTime.UtcNow + BreakerOpenDuration; // Можно увеличить время? Пока нет.
                                         _breakerState = CircuitBreakerState.Open;
                                         AddLogMessage("Circuit Breaker снова РАЗОМКНУТ после неудачной попытки в Half-Open.");
                                     }
                                 }
                            }
                            
                            // Сбрасываем счетчик последовательных ошибок 429, т.к. ошибка была НЕ 429 (или мы уже разомкнули CB)
                            // Хотя, если ошибка в HalfOpen была 429, то счетчик уже увеличился выше
                            // Подумать над этим моментом. Пока оставим так.
                            
                            break; // Выход из цикла при других ошибках или последней попытке 429
                        }
                    } // Конец for loop

                    // Если после всех попыток загрузка не удалась
                    if (!downloadSucceeded)
                    {
                        // downloadResult будет содержать информацию о последней ошибке
                        throw new Exception($"Не удалось скачать файл '{originalFileName}' после {maxRetries} попыток. Последняя ошибка: {downloadResult?.ErrorMessage ?? "Неизвестная ошибка"}");
                    }

                    // Проверка, что файл существует (дополнительная, т.к. WebGetAsync должен это гарантировать при Success)
                    FileInfo fileInfoCheck = new FileInfo(fileDocument);
                    if (!fileInfoCheck.Exists || fileInfoCheck.Length != fileSize)
                    {
                         AddLogMessage($"ПРЕДУПРЕЖДЕНИЕ: Несоответствие файла после успешной загрузки '{originalFileName}'. Ожидался размер {fileSize}, файл существует: {fileInfoCheck.Exists}, реальный размер: {(fileInfoCheck.Exists ? fileInfoCheck.Length.ToString() : "N/A")}");
                         // Можно решить, считать ли это критической ошибкой
                         // throw new Exception($"Файл '{originalFileName}' поврежден или отсутствует после скачивания.");
                    }

                    // --- ДОБАВЛЕНО: Распаковка архивов ---
                    string extension = Path.GetExtension(fileDocument).ToLowerInvariant();
                    // Расширяем список поддерживаемых форматов архивов
                    if (extension == ".zip" || extension == ".rar" || extension == ".7z" || extension == ".tar" || extension == ".gz" || extension == ".bz2" || extension == ".tar.gz" || extension == ".tar.bz2")
                    {
                        string extractionPath = Path.Combine(Path.GetDirectoryName(fileDocument), Path.GetFileNameWithoutExtension(fileDocument));
                        AddLogMessage($"Обнаружен архив '{originalFileName}'. Попытка распаковки в: {extractionPath}");
                        try
                        {
                            // Проверяем, не является ли путь системным
                            string[] protectedPaths = { "C:\\Windows", "C:\\Program Files", "C:\\Program Files (x86)" };
                            foreach (var path in protectedPaths)
                            {
                                if (extractionPath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                                {
                                    throw new UnauthorizedAccessException($"Распаковка в системную директорию {extractionPath} запрещена");
                                }
                            }

                            // Проверяем свободное место на диске (примерно в 2 раза больше размера архива)
                            var driveInfo = new DriveInfo(Path.GetPathRoot(extractionPath));
                            if (driveInfo.IsReady && driveInfo.AvailableFreeSpace < fileSize * 2)
                            {
                                AddLogMessage($"Предупреждение: На диске может быть недостаточно свободного места для распаковки. Доступно: {FormatBytes(driveInfo.AvailableFreeSpace)}", "Warning");
                            }

                            // Создаем папку для распаковки, если не существует
                            if (!Directory.Exists(extractionPath))
                            {
                                Directory.CreateDirectory(extractionPath);
                                AddLogMessage($"Создана директория для распаковки: {extractionPath}");
                            }
                            else
                            {
                                AddLogMessage($"Директория для распаковки уже существует: {extractionPath}");
                            }

                            // Используем SharpCompress для обработки архива
                            using (var archive = SharpCompress.Archives.ArchiveFactory.Open(fileDocument))
                            {
                                var options = new SharpCompress.Common.ExtractionOptions
                                {
                                    ExtractFullPath = true,
                                    Overwrite = true,
                                    PreserveFileTime = true
                                };

                                // Проверка на пустой архив
                                if (!archive.Entries.Any())
                                {
                                    AddLogMessage($"Предупреждение: Архив '{originalFileName}' не содержит файлов", "Warning");
                                }

                                // Предварительная проверка архива
                                foreach (var entry in archive.Entries)
                                {
                                    // Проверка на подозрительные имена файлов или пути
                                    if (entry.Key.Contains("..") || entry.Key.StartsWith("/") || entry.Key.StartsWith("\\"))
                                    {
                                        AddLogMessage($"Предупреждение: Архив содержит потенциально опасный путь: {entry.Key}", "Warning");
                                    }

                                    // Обнаружение и уведомление о вложенных архивах
                                    string entryExt = Path.GetExtension(entry.Key).ToLowerInvariant();
                                    if (entryExt == ".zip" || entryExt == ".rar" || entryExt == ".7z")
                                    {
                                        AddLogMessage($"Информация: Обнаружен вложенный архив в архиве: {entry.Key}", "Info");
                                    }

                                    // Проверка токена отмены
                                    if (token.IsCancellationRequested)
                                    {
                                        throw new OperationCanceledException(token);
                                    }
                                }

                                // Распаковка только файлов (не директорий)
                                int extractedFilesCount = 0;
                                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                                {
                                    // Проверка токена отмены
                                    if (token.IsCancellationRequested)
                                    {
                                        throw new OperationCanceledException(token);
                                    }

                                    try
                                    {
                                        entry.WriteToDirectory(extractionPath, options);
                                        extractedFilesCount++;
                                    }
                                    catch (Exception exEntry)
                                    {
                                        AddLogMessage($"Ошибка при распаковке файла {entry.Key}: {exEntry.Message}", "Warning");
                                        // Продолжаем с другими файлами
                                    }
                                }
                                
                                AddLogMessage($"Архив '{originalFileName}' успешно распакован в {extractionPath}. Извлечено файлов: {extractedFilesCount}");
                            }

                            // Обрабатываем вложенные архивы, если они есть
                            await ScanAndExtractNestedArchivesAsync(extractionPath, token);

                            // --- ВЫЗОВ ARCHIVE SERVICE ДЛЯ РАСПАКОВАННЫХ ФАЙЛОВ ---
                            AddLogMessage($"Запуск архивации содержимого \'{originalFileName}\' из {extractionPath}");
                            
                            // Create DocumentMeta object for the archive context
                            var metaForArchive = new FileDownloader.Models.DocumentMeta
                            {
                                documentMetaPathID = documentMetaPathID, // From DataRow
                                urlID = urlIdFromDb,             // From DataRow
                                documentMetaID = documentMetaID,     // From DataRow
                                processID = CurrentSettings.ProcessId, // From settings
                                databaseName = databaseName          // From method argument
                                // Populate other fields if needed
                            };
                            
                            // Ensure IAC connection string is available
                            if (string.IsNullOrEmpty(_iacConnectionString))
                            {
                                AddLogMessage("Ошибка: Строка подключения IAC не настроена. Невозможно заархивировать распакованные файлы.", "Error");
                            }
                            else
                            {
                                string archiveDestPath = CurrentSettings.ArchiveDestinationPath;
                                if (string.IsNullOrWhiteSpace(archiveDestPath))
                                {
                                    AddLogMessage("Ошибка: Путь для архивации (ArchiveDestinationPath) не настроен. Невозможно заархивировать распакованные файлы.", "Error");
                                }
                                else
                                {
                                    bool archiveServiceSuccess = false;
                                    try
                                    {
                                        _archiveServiceForExtractedFiles = new ArchiveService(_serverOfficeConnectionString);
                                        
                                        // Subscribe to the event before calling
                                        _archiveServiceForExtractedFiles.FileArchived += HandleExtractedFileArchived;
                                        
                                        // Call ArchiveFileMove for the directory containing extracted files
                                        _archiveServiceForExtractedFiles.ArchiveFileMove(extractionPath, archiveDestPath, metaForArchive);
                                        
                                        // Unsubscribe after the call is complete
                                        _archiveServiceForExtractedFiles.FileArchived -= HandleExtractedFileArchived;
                                        _archiveServiceForExtractedFiles = null; // Release instance
                                        
                                        AddLogMessage($"Архивация содержимого \'{originalFileName}\' завершена.", "Success");
                                        archiveServiceSuccess = true;

                                        // --- ОЧИСТКА ПОСЛЕ УСПЕШНОЙ АРХИВАЦИИ --- 
                                        // Delete original archive
                                        try
                                        {
                                            File.Delete(fileDocument);
                                            AddLogMessage($"Исходный архив \'{originalFileName}\' удален после архивации содержимого.");
                                        }
                                        catch (Exception deleteEx)
                                        {
                                            AddLogMessage($"Ошибка при удалении исходного архива \'{originalFileName}\' после архивации: {deleteEx.Message}", "Error");
                                        }

                                        // Delete extraction directory
                                        try
                                        {
                                            Directory.Delete(extractionPath, true); // Recursive delete
                                            AddLogMessage($"Временная директория распаковки \'{extractionPath}\' удалена.");
                                        }
                                        catch (Exception deleteEx)
                                        {
                                            AddLogMessage($"Ошибка при удалении директории распаковки \'{extractionPath}\': {deleteEx.Message}", "Error");
                                        }
                                        // --- КОНЕЦ ОЧИСТКИ --- 
                                    }
                                    catch (Exception archiveEx)
                                    {
                                        // Log error from ArchiveService
                                        AddLogMessage($"Ошибка во время архивации содержимого \'{originalFileName}\': {archiveEx.Message}", "Error");
                                        
                                        // Ensure event handler is unsubscribed even on error
                                        if (_archiveServiceForExtractedFiles != null)
                                        {
                                            _archiveServiceForExtractedFiles.FileArchived -= HandleExtractedFileArchived;
                                            _archiveServiceForExtractedFiles = null;
                                        }

                                        // Если архивация не удалась, спрашиваем, хотим ли мы очистить временные файлы
                                        if (!archiveServiceSuccess)
                                        {
                                            // TODO: Можно добавить диалог с вопросом об удалении временных файлов
                                            AddLogMessage("Временная директория распаковки оставлена из-за ошибки архивации.", "Warning");
                                        }
                                    }
                                }
                            }
                            // --- КОНЕЦ ВЫЗОВА ARCHIVE SERVICE ---
                        }
                        catch (OperationCanceledException) 
                        { 
                            AddLogMessage($"Распаковка архива '{originalFileName}' отменена пользователем.", "Warning");
                            throw; 
                        }
                        catch (Exception ex)
                        {
                            AddLogMessage($"Ошибка при распаковке архива '{originalFileName}': {ex.Message}", "Error");
                            
                            // Очистка временных файлов при ошибке
                            try
                            {
                                if (Directory.Exists(extractionPath))
                                {
                                    Directory.Delete(extractionPath, true);
                                    AddLogMessage($"Временная директория распаковки '{extractionPath}' удалена после ошибки.");
                                }
                            }
                            catch(Exception cleanupEx)
                            {
                                AddLogMessage($"Ошибка при очистке временной директории: {cleanupEx.Message}", "Error");
                            }
                            
                            if (!IgnoreDownloadErrors)
                            {
                                throw; // Прерываем обработку файла при ошибке, если не установлен флаг игнорирования
                            }
                        }
                        // --- КОНЕЦ БЛОКА РАСПАКОВКИ ---
                    }
                    else // Not an archive, process normally
                    {
                        // --- Выгрузка на FTP (если srcID == 1) ---
                        if (srcID == 1)
                        {
                             if (string.IsNullOrWhiteSpace(ftp) || string.IsNullOrWhiteSpace(fileNameFtp)) {
                                  throw new InvalidOperationException($"Не указаны параметры FTP (ftp='{ftp}', fileNameFtp='{fileNameFtp}') для srcID=1, documentMetaID={documentMetaID}");
                             }
                            AddLogMessage($"Выгрузка на FTP: {fileDocument} -> {ftp} (Имя: {fileNameFtp})");
                            try
                            {
                                await FtpUploadAsync(CurrentSettings, fileDocument, fileNameFtp, token, progress);
                                AddLogMessage($"Файл '{fileNameFtp}' выгружен на FTP.");
                            }
                            catch (Exception ftpEx)
                            {
                                AddLogMessage($"Ошибка выгрузки на FTP для файла '{fileNameFtp}': {ftpEx.Message}");
                                 throw; // Критичная ошибка, прерываем обработку файла
                            }
                        }

                        // --- Запись метаданных в базу IAC ---
                        AddLogMessage($"Запись метаданных в IAC для documentMetaID: {documentMetaID}");
                        using (SqlConnection conBaseI = new SqlConnection(_serverOfficeConnectionString)) 
                        {
                             await conBaseI.OpenAsync(token);
                             using (SqlCommand cmdInsert = new SqlCommand("documentMetaPathInsert", conBaseI) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 })
                             {
                                  cmdInsert.Parameters.Add("@databaseName", SqlDbType.VarChar, 50).Value = databaseName;
                                  cmdInsert.Parameters.Add("@computerName", SqlDbType.VarChar, 50).Value = computerName;
                                  cmdInsert.Parameters.Add("@directoryName", SqlDbType.VarChar, 50).Value = directoryName;
                                  cmdInsert.Parameters.Add("@processID", SqlDbType.Int).Value = CurrentSettings.ProcessId;
                                  cmdInsert.Parameters.Add("@themeID", SqlDbType.Int).Value = themeId;
                                  cmdInsert.Parameters.Add("@year", SqlDbType.Int).Value = publishDate.Year;
                                  cmdInsert.Parameters.Add("@month", SqlDbType.Int).Value = publishDate.Month;
                                  cmdInsert.Parameters.Add("@day", SqlDbType.Int).Value = publishDate.Day;

                                  if (databaseName == "fcsNotification" || databaseName == "contract" || databaseName == "purchaseNotice" || databaseName == "requestQuotation")
                                  {
                                      cmdInsert.Parameters.Add("@urlIDText", SqlDbType.VarChar, 50).Value = urlIdFromDb?.ToString() ?? (object)DBNull.Value;
                                      cmdInsert.Parameters.Add("@urlID", SqlDbType.Int).Value = DBNull.Value;
                                  }
                                  else
                                  {
                                      cmdInsert.Parameters.Add("@urlIDText", SqlDbType.VarChar, 50).Value = DBNull.Value;
                                      if (urlIdFromDb != null && int.TryParse(urlIdFromDb.ToString(), out int urlIdInt))
                                          cmdInsert.Parameters.Add("@urlID", SqlDbType.Int).Value = urlIdInt;
                                      else
                                          cmdInsert.Parameters.Add("@urlID", SqlDbType.Int).Value = DBNull.Value;
                                  }

                                  cmdInsert.Parameters.Add("@documentMetaID", SqlDbType.Int).Value = documentMetaID;
                                  cmdInsert.Parameters.Add("@fileName", SqlDbType.VarChar, 250).Value = originalFileName;
                                  cmdInsert.Parameters.Add("@suffixName", SqlDbType.VarChar, 50).Value = suffixName;
                                  cmdInsert.Parameters.Add("@expName", SqlDbType.VarChar, 10).Value = expName;
                                  cmdInsert.Parameters.Add("@docDescription", SqlDbType.VarChar, 250).Value = docDescription;
                                  cmdInsert.Parameters.Add("@fileSize", SqlDbType.Decimal).Value = fileSize;
                                  cmdInsert.Parameters.Add("@srcID", SqlDbType.Int).Value = srcID;
                                  cmdInsert.Parameters.Add("@usrID", SqlDbType.Int).Value = CurrentSettings.UserId;
                                  cmdInsert.Parameters.Add("@documentMetaPathID", SqlDbType.Int).Value = documentMetaPathID;

                                  await cmdInsert.ExecuteNonQueryAsync(token);
                                  AddLogMessage($"Метаданные для documentMetaID: {documentMetaID} записаны в IAC.");
                             }
                        }


                        // --- Обновление флага в основной базе (для srcID 0 и 1) ---
                        if (srcID == 0 || srcID == 1)
                        {
                             AddLogMessage($"Обновление флага для documentMetaID: {documentMetaID} в базе {databaseName}");
                             using (SqlConnection conBase = new SqlConnection(targetDbConnectionString))
                             {
                                  await conBase.OpenAsync(token);
                                  using (SqlCommand cmdUpdate = new SqlCommand("documentMetaUpdateFlag", conBase) { CommandType = CommandType.StoredProcedure })
                                  {
                                       cmdUpdate.Parameters.Add("@documentMetaID", SqlDbType.Int).Value = documentMetaID;
                                       await cmdUpdate.ExecuteNonQueryAsync(token);
                                       AddLogMessage($"Флаг для documentMetaID: {documentMetaID} обновлен.");
                                  }
                             }
                        }

                         // --- Удаление временного файла (для srcID == 1) ---
                        if (srcID == 1 && File.Exists(fileDocument))
                        {
                             AddLogMessage($"Удаление временного файла после FTP: {fileDocument}");
                             File.Delete(fileDocument);
                        }

                        // Обновляем информацию о текущем файле
                        CurrentFileDetails = $"Файл: {originalFileName}\n" +
                                           $"Размер: {fileSize} байт\n" +
                                           $"Дата публикации: {publishDate:dd.MM.yyyy}\n" +
                                           $"Описание: {docDescription}";
                        _lastProcessedFileName = originalFileName; // Update last processed file name

                    }
                }
                catch (OperationCanceledException) { throw; } // Просто перебрасываем отмену
                catch (Exception webEx) // Ловит исключение из цикла или из блока выше
                {
                    AddLogMessage($"Ошибка при скачивании или проверке файла '{originalFileName}': {webEx.Message}");
                    // WebGetAsync должен сам удалять временный файл при ошибке, но проверим на всякий случай
                    if (File.Exists(fileDocument) && downloadResult != null && !downloadResult.Success)
                    {
                        try { File.Delete(fileDocument); } catch { /* Ignore delete error */ }
                    }
                    throw; // Перебрасываем ошибку дальше
                }

                // --- Выгрузка на FTP (если srcID == 1) ---
                if (srcID == 1)
                {
                     if (string.IsNullOrWhiteSpace(ftp) || string.IsNullOrWhiteSpace(fileNameFtp)) {
                          throw new InvalidOperationException($"Не указаны параметры FTP (ftp='{ftp}', fileNameFtp='{fileNameFtp}') для srcID=1, documentMetaID={documentMetaID}");
                     }
                    AddLogMessage($"Выгрузка на FTP: {fileDocument} -> {ftp} (Имя: {fileNameFtp})");
                    try
                    {
                        await FtpUploadAsync(CurrentSettings, fileDocument, fileNameFtp, token, progress);
                        AddLogMessage($"Файл '{fileNameFtp}' выгружен на FTP.");
                    }
                    catch (Exception ftpEx)
                    {
                        AddLogMessage($"Ошибка выгрузки на FTP для файла '{fileNameFtp}': {ftpEx.Message}");
                         throw; // Критичная ошибка, прерываем обработку файла
                    }
                }

                // --- Запись метаданных в базу IAC ---
                AddLogMessage($"Запись метаданных в IAC для documentMetaID: {documentMetaID}");
                using (SqlConnection conBaseI = new SqlConnection(_serverOfficeConnectionString)) 
                {
                     await conBaseI.OpenAsync(token);
                     using (SqlCommand cmdInsert = new SqlCommand("documentMetaPathInsert", conBaseI) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 })
                     {
                          cmdInsert.Parameters.Add("@databaseName", SqlDbType.VarChar, 50).Value = databaseName;
                          cmdInsert.Parameters.Add("@computerName", SqlDbType.VarChar, 50).Value = computerName;
                          cmdInsert.Parameters.Add("@directoryName", SqlDbType.VarChar, 50).Value = directoryName;
                          cmdInsert.Parameters.Add("@processID", SqlDbType.Int).Value = CurrentSettings.ProcessId;
                          cmdInsert.Parameters.Add("@themeID", SqlDbType.Int).Value = themeId;
                          cmdInsert.Parameters.Add("@year", SqlDbType.Int).Value = publishDate.Year;
                          cmdInsert.Parameters.Add("@month", SqlDbType.Int).Value = publishDate.Month;
                          cmdInsert.Parameters.Add("@day", SqlDbType.Int).Value = publishDate.Day;

                          if (databaseName == "fcsNotification" || databaseName == "contract" || databaseName == "purchaseNotice" || databaseName == "requestQuotation")
                          {
                              cmdInsert.Parameters.Add("@urlIDText", SqlDbType.VarChar, 50).Value = urlIdFromDb?.ToString() ?? (object)DBNull.Value;
                              cmdInsert.Parameters.Add("@urlID", SqlDbType.Int).Value = DBNull.Value;
                          }
                          else
                          {
                              cmdInsert.Parameters.Add("@urlIDText", SqlDbType.VarChar, 50).Value = DBNull.Value;
                              if (urlIdFromDb != null && int.TryParse(urlIdFromDb.ToString(), out int urlIdInt))
                                  cmdInsert.Parameters.Add("@urlID", SqlDbType.Int).Value = urlIdInt;
                              else
                                  cmdInsert.Parameters.Add("@urlID", SqlDbType.Int).Value = DBNull.Value;
                          }

                          cmdInsert.Parameters.Add("@documentMetaID", SqlDbType.Int).Value = documentMetaID;
                          cmdInsert.Parameters.Add("@fileName", SqlDbType.VarChar, 250).Value = originalFileName;
                          cmdInsert.Parameters.Add("@suffixName", SqlDbType.VarChar, 50).Value = suffixName;
                          cmdInsert.Parameters.Add("@expName", SqlDbType.VarChar, 10).Value = expName;
                          cmdInsert.Parameters.Add("@docDescription", SqlDbType.VarChar, 250).Value = docDescription;
                          cmdInsert.Parameters.Add("@fileSize", SqlDbType.Decimal).Value = fileSize;
                          cmdInsert.Parameters.Add("@srcID", SqlDbType.Int).Value = srcID;
                          cmdInsert.Parameters.Add("@usrID", SqlDbType.Int).Value = CurrentSettings.UserId;
                          cmdInsert.Parameters.Add("@documentMetaPathID", SqlDbType.Int).Value = documentMetaPathID;

                          await cmdInsert.ExecuteNonQueryAsync(token);
                          AddLogMessage($"Метаданные для documentMetaID: {documentMetaID} записаны в IAC.");
                     }
                }


                // --- Обновление флага в основной базе (для srcID 0 и 1) ---
                if (srcID == 0 || srcID == 1)
                {
                     AddLogMessage($"Обновление флага для documentMetaID: {documentMetaID} в базе {databaseName}");
                     using (SqlConnection conBase = new SqlConnection(targetDbConnectionString))
                     {
                          await conBase.OpenAsync(token);
                          using (SqlCommand cmdUpdate = new SqlCommand("documentMetaUpdateFlag", conBase) { CommandType = CommandType.StoredProcedure })
                          {
                               cmdUpdate.Parameters.Add("@documentMetaID", SqlDbType.Int).Value = documentMetaID;
                               await cmdUpdate.ExecuteNonQueryAsync(token);
                               AddLogMessage($"Флаг для documentMetaID: {documentMetaID} обновлен.");
                          }
                     }
                }

                 // --- Удаление временного файла (для srcID == 1) ---
                if (srcID == 1 && File.Exists(fileDocument))
                {
                     AddLogMessage($"Удаление временного файла после FTP: {fileDocument}");
                     File.Delete(fileDocument);
                }

                // Обновляем информацию о текущем файле
                CurrentFileDetails = $"Файл: {originalFileName}\n" +
                                   $"Размер: {fileSize} байт\n" +
                                   $"Дата публикации: {publishDate:dd.MM.yyyy}\n" +
                                   $"Описание: {docDescription}";

                // Сбрасываем счетчики при начале загрузки нового файла
                _totalBytesDownloaded = 0;
                _lastBytesDownloaded = 0;
                _lastProgressUpdate = DateTime.Now;
                _downloadStartTime = DateTime.Now;

                // В методе WebGetAsync добавляем обновление прогресса
                progress?.Report((double)_totalBytesDownloaded / fileSize * 100);
                UpdateDownloadStats(fileSize);

                // --- Обновляем поле с именем файла для таймера ---
                _lastProcessedFileName = originalFileName;
            }
            else // Режим проверки ошибок (flProv == true)
            {
                AddLogMessage($"Проверка файла: {fileDocument}");
                FileInfo fileInfo = new FileInfo(fileDocument);

                bool deleteFile = false;
                string deleteReason = "";

                if (!fileInfo.Exists)
                {
                     AddLogMessage($"Файл {fileDocument} не найден. Проверка пропущена.");
                     // Возможно, стоит вызвать documentMetaUpdateFlagDelete, если файла нет? Уточнить логику.
                     // deleteFile = true; deleteReason = "Файл не существует.";
                }
                else
                {
                     if (fileInfo.Length > 0 && fileInfo.Length < 700)
                     {
                         deleteFile = true;
                         deleteReason = $"Файл слишком маленький ({fileInfo.Length} байт).";
                     }
                     else if (fileInfo.Length == 0)
                     {
                          deleteFile = true;
                          deleteReason = "Файл пустой (0 байт).";
                     }
                     else
                     {
                          AddLogMessage($"Файл {fileDocument} существует, размер {fileInfo.Length} байт. Проверка пройдена.");
                     }
                }


                if (deleteFile)
                {
                     AddLogMessage($"Удаление некорректного файла: {fileDocument}. Причина: {deleteReason}");
                     try
                     {
                         if (fileInfo.Exists) // Удаляем только если он есть
                         {
                             fileInfo.Delete();
                         }

                         // Выполняем процедуру удаления флага/записи
                         AddLogMessage($"Вызов documentMetaUpdateFlagDelete для documentMetaID: {documentMetaID}");
                         using (SqlConnection conBase = new SqlConnection(targetDbConnectionString))
                         {
                              await conBase.OpenAsync(token);
                              using (SqlCommand cmdUpdateDelete = new SqlCommand("documentMetaUpdateFlagDelete", conBase) { CommandType = CommandType.StoredProcedure })
                              {
                                   cmdUpdateDelete.Parameters.Add("@documentMetaID", SqlDbType.Int).Value = documentMetaID;
                                   await cmdUpdateDelete.ExecuteNonQueryAsync(token);
                                   AddLogMessage($"Запись для documentMetaID: {documentMetaID} помечена на удаление.");
                              }
                         }
                     }
                     catch (Exception delEx)
                     {
                          AddLogMessage($"ОШИБКА при удалении файла или обновлении флага для documentMetaID {documentMetaID}: {delEx.Message}");
                         throw; // Перебрасываем
                     }
                }
            }
        }
        finally
        {
            // --- Задержка после обработки файла ---
            // Эта задержка выполняется ВСЕГДА после попытки обработки файла (успешной или нет),
            // чтобы снизить общую частоту запросов к серверу для СЛЕДУЮЩЕГО файла.
            if (!token.IsCancellationRequested) // Не ждем, если отмена
            {
                await Task.Delay(CurrentSettings.SleepIntervalMilliseconds, CancellationToken.None); // Используем CancellationToken.None, чтобы задержка выполнилась даже при отмене *во время* ее ожидания
            }
        }
    } // Конец метода ProcessFileAsync

    private void UpdateDownloadStats(long totalFileSize)
    {
        var now = DateTime.Now;
        var timeElapsed = (now - _lastProgressUpdate).TotalSeconds;
        
        if (timeElapsed > 0)
        {
            var bytesPerSecond = (_totalBytesDownloaded - _lastBytesDownloaded) / timeElapsed;
            DownloadSpeed = FormatBytes(bytesPerSecond) + "/сек";
            
            if (bytesPerSecond > 0)
            {
                var remainingBytes = totalFileSize - _totalBytesDownloaded;
                var secondsRemaining = remainingBytes / bytesPerSecond;
                EstimatedTimeRemaining = FormatTimeSpan(TimeSpan.FromSeconds(secondsRemaining));
            }
        }
        
        _lastBytesDownloaded = _totalBytesDownloaded;
        _lastProgressUpdate = now;
    }

    private string FormatBytes(double bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        while (bytes >= 1024 && order < sizes.Length - 1)
        {
            order++;
            bytes = bytes / 1024;
        }
        return $"{bytes:0.##} {sizes[order]}";
    }

    private string FormatTimeSpan(TimeSpan timeSpan)
    {
        if (timeSpan.TotalHours >= 1)
            return $"{(int)timeSpan.TotalHours}ч {timeSpan.Minutes}м";
        if (timeSpan.TotalMinutes >= 1)
            return $"{timeSpan.Minutes}м {timeSpan.Seconds}с";
        return $"{timeSpan.Seconds}с";
    }

    // --- ЗАМЕНА WebGetAsync ---
    private async Task<DownloadResult> WebGetAsync(string url, string tempFilePath, CancellationToken token, IProgress<double> progress)
    {
        var result = new DownloadResult { Success = false, ActualSize = 0, TempFilePath = tempFilePath };
        FileLogger.Log($"WebGetAsync: Начало загрузки {url} -> {tempFilePath}");

        // Логируем заголовки запроса
        FileLogger.Log($"WebGetAsync: Заголовки запроса:");
        foreach (var header in _httpClient.DefaultRequestHeaders)
        {
            FileLogger.Log($"    {header.Key}: {string.Join(", ", header.Value)}");
        }

        // Ensure directory exists (optional here, could be done just before Move)
        try
        {
            string tempDirectory = Path.GetDirectoryName(tempFilePath);
            if (!string.IsNullOrEmpty(tempDirectory) && !Directory.Exists(tempDirectory))
            {
                Directory.CreateDirectory(tempDirectory);
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Ошибка создания каталога [{Path.GetDirectoryName(tempFilePath)}]: {ex.Message}";
            FileLogger.Log($"WebGetAsync: Ошибка создания каталога: {result.ErrorMessage}");
            return result;
        }

        try
        {
            // Use static HttpClient instance
            using (HttpResponseMessage response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                result.StatusCode = response.StatusCode;
                result.RetryAfterHeaderValue = response.Headers.RetryAfter?.Delta; // <-- Сохраняем значение Retry-After
                
                // Логируем заголовки ответа
                FileLogger.Log($"WebGetAsync: Заголовки ответа:");
                foreach (var header in response.Headers)
                {
                    FileLogger.Log($"    {header.Key}: {string.Join(", ", header.Value)}");
                }
                if (response.Content?.Headers != null)
                {
                    foreach (var header in response.Content.Headers)
                    {
                        FileLogger.Log($"    {header.Key}: {string.Join(", ", header.Value)}");
                    }
                }

                FileLogger.Log($"WebGetAsync: Получен ответ {result.StatusCode} для {url}");

                if (response.IsSuccessStatusCode)
                {
                    result.ExpectedSize = response.Content.Headers.ContentLength;
                    FileLogger.Log($"WebGetAsync: ContentLength={(result.ExpectedSize.HasValue ? result.ExpectedSize.Value.ToString() : "N/A")} для {url}");

                    // Download to temp file stream
                    using (Stream contentStream = await response.Content.ReadAsStreamAsync())
                    using (FileStream fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true)) // Use async flag
                    {
                        // Use CopyToAsync with buffer size and cancellation token
                         await contentStream.CopyToAsync(fileStream, 8192, token);
                    }

                    token.ThrowIfCancellationRequested(); // Check cancellation after writing

                    // Verify downloaded file size
                    FileInfo fileInfo = new FileInfo(tempFilePath);
                    result.ActualSize = fileInfo.Exists ? fileInfo.Length : 0;
                    FileLogger.Log($"WebGetAsync: Файл сохранен, ActualSize={result.ActualSize} для {url}");

                    // Size Check Logic
                    if (result.ExpectedSize.HasValue && result.ExpectedSize.Value != result.ActualSize)
                    {
                        result.ErrorMessage = $"Ожидаемый размер ({result.ExpectedSize.Value} байт) не совпадает с фактическим ({result.ActualSize} байт).";
                        FileLogger.Log($"WebGetAsync: Ошибка размера для {url}: {result.ErrorMessage}");
                        try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
                    }
                    else if (result.ExpectedSize.HasValue && result.ExpectedSize.Value == 0 && result.ActualSize != 0)
                    {
                         result.ErrorMessage = $"Ожидался пустой файл (Content-Length: 0), но скачан файл размером {result.ActualSize} байт.";
                         FileLogger.Log($"WebGetAsync: Ошибка (ожидался 0 байт) для {url}: {result.ErrorMessage}");
                         try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
                    }
                    else
                    {
                        result.Success = true; // Download successful and size check passed (or not applicable)
                         FileLogger.Log($"WebGetAsync: Успешная загрузка {url}");
                    }
                }
                else // HTTP request failed
                {
                    result.ErrorMessage = $"Ошибка HTTP: {(int)response.StatusCode} {response.ReasonPhrase}";
                     FileLogger.Log($"WebGetAsync: Ошибка HTTP для {url}: {result.ErrorMessage}");
                    try { result.ErrorMessage += Environment.NewLine + await response.Content.ReadAsStringAsync(); } catch { } // Try read error body
                    // Ensure temp file doesn't exist if HTTP request failed
                     try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
                }
            }
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Загрузка отменена.";
            FileLogger.Log($"WebGetAsync: Загрузка отменена для {url}");
            try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
            throw; // Rethrow cancellation
        }
        catch (HttpRequestException httpEx)
        {
            result.ErrorMessage = $"Ошибка сети: {httpEx.Message}";
            if (httpEx.InnerException != null) result.ErrorMessage += $" (Inner: {httpEx.InnerException.Message})";
             FileLogger.Log($"WebGetAsync: Ошибка HttpRequestException для {url}: {result.ErrorMessage}");
            try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
        }
        catch (IOException ioEx) // Catch specific IO errors (e.g., disk full)
        {
             result.ErrorMessage = $"Ошибка I/O: {ioEx.Message}";
             FileLogger.Log($"WebGetAsync: Ошибка IOException для {url}: {result.ErrorMessage}");
             try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Общая ошибка: {ex.Message}";
             FileLogger.Log($"WebGetAsync: Общая ошибка для {url}: {result.ErrorMessage}");
            try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
        }

        return result;
    }

    private async Task FtpUploadAsync(ApplicationSettings ftpSettings, string localFilePath, string remoteFileName, CancellationToken token, IProgress<double> progress)
    {
        if (string.IsNullOrEmpty(ftpSettings.FtpHost)) { throw new InvalidOperationException("FTP host is not configured."); }

        using (var ftpClient = new AsyncFtpClient())
        {
            ftpClient.Host = ftpSettings.FtpHost;
            ftpClient.Port = ftpSettings.FtpPort;
            if (!string.IsNullOrEmpty(ftpSettings.FtpUsername)) { ftpClient.Credentials = new NetworkCredential(ftpSettings.FtpUsername, ftpSettings.FtpPassword ?? ""); }
            
            // Настройки шифрования через Config
            ftpClient.Config.EncryptionMode = ftpSettings.FtpUseSsl ? FtpEncryptionMode.Auto : FtpEncryptionMode.None;
            if (ftpSettings.FtpUseSsl)
            {
                ftpClient.Config.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            }
            // Отключаем DataConnectionEncryption, если SSL не используется (иногда помогает)
            // ftpClient.Config.DataConnectionEncryption = ftpSettings.FtpUseSsl; 

            // Валидация сертификата
            ftpClient.ValidateCertificate += (control, e) =>
            {
                if (!ftpSettings.FtpValidateCertificate) { e.Accept = true; return; }
                if (e.PolicyErrors != System.Net.Security.SslPolicyErrors.None) { AddLogMessage($"ПРЕДУПРЕЖДЕНИЕ FTP: Ошибка сертификата: {e.PolicyErrors}"); e.Accept = false; }
                else { e.Accept = true; }
            };

            try
            {
                AddLogMessage($"Подключение к FTP: {ftpSettings.FtpHost}:{ftpSettings.FtpPort}...");
                await ftpClient.Connect(token);
                AddLogMessage("FTP подключение установлено.");

                string remotePath = remoteFileName; // TODO: Уточнить логику пути
                AddLogMessage($"Загрузка на FTP: {localFilePath} -> {remotePath}");

                // UploadFile возвращает FtpStatus
                var status = await ftpClient.UploadFile(localFilePath, remotePath, FtpRemoteExists.Overwrite, true, FtpVerify.None, null, token);

                // Проверяем статус
                if (status != FtpStatus.Success)
                { 
                    // Генерируем стандартное исключение с информацией из статуса, если она есть (не всегда)
                    string errorMessage = $"FTP upload failed with status: {status}."; 
                    // Можно проверить ftpClient.LastReply для деталей
                    // if(ftpClient.LastReply != null) errorMessage += $" Last Reply: {ftpClient.LastReply.Code} {ftpClient.LastReply.Message}";
                    AddLogMessage($"ОШИБКА FTP при загрузке файла: {errorMessage}");
                    throw new Exception(errorMessage); // Используем стандартное Exception
                }
                AddLogMessage("FTP загрузка завершена.");
            }
            catch (OperationCanceledException) { AddLogMessage($"Загрузка на FTP отменена: {localFilePath}"); throw; }
            catch (FtpCommandException ftpCmdEx) // Используем using FluentFTP.Exceptions;
            {
                 // Логируем только сообщение, без .Code
                 AddLogMessage($"ОШИБКА команды FTP: {ftpCmdEx.Message}"); 
                 throw; // Перебрасываем
            }
            catch (Exception ex) 
            { 
                // Ловим другие возможные ошибки (IOException, SocketException и т.д.)
                AddLogMessage($"ОШИБКА FTP: {ex.GetType().Name} - {ex.Message}"); 
                throw; 
            }
            finally { if (ftpClient.IsConnected) { await ftpClient.Disconnect(token); AddLogMessage("FTP соединение закрыто."); } }
        }
    }

    private void AddLogMessage(string message, string type = "Info")
    {
        var logMessage = new LogMessage
        {
            Message = message,
            Type = type,
            Timestamp = DateTime.Now
        };

        // Просто добавляем сообщение в потокобезопасную очередь
        _logMessageQueue.Enqueue(logMessage);
    }

    private void UpdateFilteredLogMessages()
    {
        // Этот метод вызывается из UpdateUiFromTimerTick в UI потоке

        IEnumerable<LogMessage> messagesToShow;
        if (SelectedLogFilterType == null || SelectedLogFilterType.Type == "All")
        {
            messagesToShow = LogMessages; // Показываем все сообщения
        }
        else
        {
            messagesToShow = LogMessages.Where(m => m.Type == SelectedLogFilterType.Type);
        }

        // Оптимизация: Очищаем существующую коллекцию и добавляем элементы
        // вместо создания новой коллекции каждый раз.
        _filteredLogMessages.Clear();
        foreach (var message in messagesToShow)
        {
            _filteredLogMessages.Add(message);
        }
        // Уведомление об изменении коллекции (если Clear/Add не делают это автоматически)
        // Для стандартной ObservableCollection это не нужно, но если используется другая реализация,
        // может понадобиться вызвать OnPropertyChanged(nameof(FilteredLogMessages));
        // В данном случае, предполагаем, что Clear/Add достаточно для обновления UI.
    }

    private void InitializeLogFilterTypes()
    {
        LogFilterTypes.Clear();
        LogFilterTypes.Add(new LogFilterType { Name = "All", DisplayName = "Все сообщения", Type = "All" });
        LogFilterTypes.Add(new LogFilterType { Name = "Error", DisplayName = "Ошибки", Type = "Error" });
        LogFilterTypes.Add(new LogFilterType { Name = "Warning", DisplayName = "Предупреждения", Type = "Warning" });
        LogFilterTypes.Add(new LogFilterType { Name = "Info", DisplayName = "Информация", Type = "Info" });
        LogFilterTypes.Add(new LogFilterType { Name = "Success", DisplayName = "Успешно", Type = "Success" });
        SelectedLogFilterType = LogFilterTypes.First();
    }

    // --- Свойства для баз данных и тем ---
    private ObservableCollection<DatabaseInfo> _availableDatabases = new ObservableCollection<DatabaseInfo>();
    public ObservableCollection<DatabaseInfo> AvailableDatabases
    {
        get => _availableDatabases;
        private set => SetProperty(ref _availableDatabases, value);
    }

    // ... existing code ...

    private void LoadAvailableDatabases()
    {
        AddLogMessage($"LoadAvailableDatabases: Начало. Проверка _baseConnectionString.", "Info"); // Добавлено
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            AddLogMessage("LoadAvailableDatabases: Ошибка - _baseConnectionString пустая или null.", "Error"); // Изменено
            return;
        }
        AddLogMessage($"LoadAvailableDatabases: _baseConnectionString = '{_baseConnectionString}'.", "Info"); // Добавлено

        try
        {
            // Удаляем "notificationEF", "notificationZK", "notificationOK" из списка
            var databases = new[] { "fcsNotification", "contract", "purchaseNotice", "requestQuotation" };
            var availableDbs = new List<DatabaseInfo>();

            AddLogMessage($"LoadAvailableDatabases: Начало цикла проверки {databases.Length} баз данных.", "Info"); // Добавлено
            foreach (var db in databases)
            {
                try
                {
                    // Убираем Replace, так как базовое имя БД теперь не важно
                    var connectionString = _baseConnectionString + $";Initial Catalog={db}";
                    AddLogMessage($"LoadAvailableDatabases: Попытка подключения к {db} ({connectionString})", "Info"); // Добавлено
                    using (var connection = new SqlConnection(connectionString))
                    {
                        // Установим короткий таймаут для быстрой проверки
                        connection.Open(); // Используем стандартный таймаут

                        // Формируем DisplayName
                        string displayName;
                        switch (db)
                        {
                            case "fcsNotification": displayName = "Извещения 44 (fcsNotification)"; break;
                            case "contract": displayName = "Контракт (contract)"; break;
                            case "purchaseNotice": displayName = "Извещения 223 (purchaseNotice)"; break;
                            case "requestQuotation": displayName = "Запрос цен (requestQuotation)"; break;
                            default: displayName = db; break; // На случай, если появятся другие
                        }

                        availableDbs.Add(new DatabaseInfo { Name = db, DisplayName = displayName });
                        AddLogMessage($"LoadAvailableDatabases: База данных {displayName} доступна.", "Success"); // Изменено
                    }
                }
                catch (Exception ex)
                {
                    // Логируем подробно ошибку подключения
                    AddLogMessage($"LoadAvailableDatabases: База данных {db} недоступна. Ошибка: {ex.GetType().Name} - {ex.Message}", "Warning"); // Изменено
                }
            }
            AddLogMessage($"LoadAvailableDatabases: Цикл проверки баз данных завершен. Найдено доступных: {availableDbs.Count}.", "Info"); // Добавлено

            AvailableDatabases = new ObservableCollection<DatabaseInfo>(availableDbs);
            AddLogMessage("LoadAvailableDatabases: Загрузка списка доступных баз данных завершена.", "Info"); // Изменено
        }
        catch (Exception ex)
        {
            AddLogMessage($"LoadAvailableDatabases: КРИТИЧЕСКАЯ ОШИБКА: {ex.Message}", "Error"); // Изменено
            if (ex.InnerException != null)
            {
                AddLogMessage($"LoadAvailableDatabases: Внутренняя ошибка: {ex.InnerException.Message}", "Error"); // Изменено
            }
            AvailableDatabases.Clear(); // Очищаем на всякий случай
        }
        AddLogMessage($"LoadAvailableDatabases: Завершение метода.", "Info"); // Добавлено
    }

    // Метод для загрузки списка тем из базы IAC (zakupkiweb)
    private void LoadAvailableThemes()
    {
        AddLogMessage("LoadAvailableThemes: Загрузка тем из базы IAC (zakupkiweb)...", "Info");
        if (string.IsNullOrEmpty(_iacConnectionString))
        {
             AddLogMessage("LoadAvailableThemes: Ошибка - строка подключения IacConnection не загружена.", "Error");
             SetProperty(ref _availableThemes, new ObservableCollection<ThemeInfo>(), nameof(AvailableThemes));
             return;
        }
         AddLogMessage($"LoadAvailableThemes: Используется строка подключения _iacConnectionString='{_iacConnectionString}'", "Info");

        try
        {
            var connectionString = _iacConnectionString;
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand("SELECT ThemeID, themeName FROM Theme", connection)) // Используем правильное имя столбца themeName
                {
                    using (var reader = command.ExecuteReader())
                    {
                        var themes = new List<ThemeInfo>();
                        while (reader.Read())
                        {
                            themes.Add(new ThemeInfo
                            {
                                Id = reader.GetInt32(0), // ThemeID - индекс 0
                                Name = reader.GetString(1) // themeName - индекс 1 (остается прежним)
                            });
                        }
                        SetProperty(ref _availableThemes, new ObservableCollection<ThemeInfo>(themes), nameof(AvailableThemes));
                        AddLogMessage($"LoadAvailableThemes: Загружено {themes.Count} тем из IAC.", "Success");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AddLogMessage($"LoadAvailableThemes: Ошибка при загрузке тем из IAC: {ex.Message}", "Error");
            SetProperty(ref _availableThemes, new ObservableCollection<ThemeInfo>(), nameof(AvailableThemes));
        }
         AddLogMessage("LoadAvailableThemes: Завершение метода.", "Info");
    }

    private void LoadConfigurationAndSettings()
    {
        try
        {
            // Загружаем строки подключения как есть
            _baseConnectionString = App.Configuration.GetConnectionString("BaseConnection");
            _iacConnectionString = App.Configuration.GetConnectionString("IacConnection");
            _serverOfficeConnectionString = App.Configuration.GetConnectionString("ServerOfficeConnection");
            // Added logging to check the retrieved value
            FileLogger.Log($"Retrieved BaseConnectionString: '{_baseConnectionString}'"); 
            FileLogger.Log($"Retrieved IacConnectionString: '{_iacConnectionString}'"); // Also log the other connection strings for comparison
            FileLogger.Log($"Retrieved ServerOfficeConnectionString: '{_serverOfficeConnectionString}'");

            // Проверяем наличие ServerOfficeConnection, так как он теперь базовый
            if (string.IsNullOrEmpty(_serverOfficeConnectionString))
            {
                AddLogMessage("ОШИБКА: Строка подключения ServerOfficeConnection не найдена.");
                _baseConnectionString = null; // Не можем работать без базового сервера
            }
            else
            {
                // Формируем базовую строку из ServerOfficeConnection, убрав Initial Catalog
                var serverOfficeBuilder = new SqlConnectionStringBuilder(_serverOfficeConnectionString);
                string baseServer = serverOfficeBuilder.DataSource;
                serverOfficeBuilder.Remove("Initial Catalog"); 
                _baseConnectionString = serverOfficeBuilder.ConnectionString; 
                AddLogMessage($"LoadConfigurationAndSettings: Базовая строка подключения установлена на сервер: {baseServer} (из ServerOfficeConnection)");
                AddLogMessage($"LoadConfigurationAndSettings: _baseConnectionString = '{_baseConnectionString}'", "Info"); // Добавлено
                
                // Fetch DefaultConnection here to use it in the warning check below
                string defaultConnectionString = App.Configuration.GetConnectionString("DefaultConnection"); 

                // Если DefaultConnection существует и отличается от базового, логируем предупреждение
                if (!string.IsNullOrEmpty(defaultConnectionString))
                {
                    var defaultBuilder = new SqlConnectionStringBuilder(defaultConnectionString);
                    if (!string.Equals(defaultBuilder.DataSource, baseServer, StringComparison.OrdinalIgnoreCase))
                    {
                        AddLogMessage($"ПРЕДУПРЕЖДЕНИЕ: DefaultConnection ({defaultBuilder.DataSource}) отличается от базового сервера ({baseServer}) и больше не используется для основных операций.");
                    }
                }
            }

            // Загрузка настроек приложения
            var appSettings = App.Configuration.GetSection("AppSettings").Get<ApplicationSettings>() ?? new ApplicationSettings();
            CurrentSettings = appSettings;
            // Check if archive path is valid, provide default if not
            if (string.IsNullOrWhiteSpace(CurrentSettings.ArchiveDestinationPath))
            {
                CurrentSettings.ArchiveDestinationPath = "C:\\FileArchives"; // Default value
                AddLogMessage($"Путь для архивации не указан или некорректен в appsettings.json, используется путь по умолчанию: {CurrentSettings.ArchiveDestinationPath}", "Warning");
            }
            AddLogMessage($"Настройки загружены. Потоков: {CurrentSettings.MaxParallelDownloads}, Пауза: {CurrentSettings.SleepIntervalMilliseconds} мс, Путь архива: {CurrentSettings.ArchiveDestinationPath}");

            // Загрузка настроек FTP
            var ftpSettings = App.Configuration.GetSection("FtpSettings").Get<FtpSettings>() ?? new FtpSettings();
            CurrentFtpSettings = ftpSettings;
        }
        catch (Exception ex)
        {
            AddLogMessage($"КРИТИЧЕСКАЯ ОШИБКА при загрузке конфигурации: {ex.Message}");
            // Handle critical error, maybe prevent application from fully starting
            CurrentSettings = new ApplicationSettings(); // Default settings
            CurrentFtpSettings = new FtpSettings(); // Default settings
            _baseConnectionString = null;
            _iacConnectionString = null;
            _serverOfficeConnectionString = null;
        }
    }

    private void OpenSettingsWindow()
    {
        var settingsViewModel = new SettingsViewModel(new ApplicationSettings(CurrentSettings));
        settingsViewModel.OnSave = ApplySettings;
        var settingsWindow = new SettingsWindow(settingsViewModel) { Owner = Application.Current.MainWindow };
        settingsWindow.ShowDialog();
    }

    private void ApplySettings(ApplicationSettings newSettings)
    {
        CurrentSettings = newSettings;
        AddLogMessage($"Настройки применены. Потоков: {CurrentSettings.MaxParallelDownloads}, Пауза: {CurrentSettings.SleepIntervalMilliseconds} мс");
    }

    private void CancelDownload()
    {
        if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
        {
            AddLogMessage("Запрос на отмену операции...");
            _cancellationTokenSource.Cancel();
        }
    }

    // --- Реализация IDataErrorInfo --- 

    // Свойство Error возвращает общую ошибку объекта (обычно не используется в WPF для привязки)
    public string Error => null; // Не реализуем общую ошибку

    // Индексатор this[] возвращает сообщение об ошибке для конкретного свойства
    public string this[string columnName]
    {
        get
        {
            string error = string.Empty;
            switch (columnName)
            {
                case nameof(SelectedDatabase):
                    if (SelectedDatabase == null)
                        error = "Необходимо выбрать базу данных";
                    break;
                
                case nameof(SelectedTheme):
                    // Если тема всегда обязательна
                    if (SelectedTheme == null)
                        error = "Необходимо выбрать тему";
                    // Можно добавить другие проверки для темы, если нужно
                    break;
                
                case nameof(BeginDate):
                case nameof(EndDate):
                    if (BeginDate > EndDate)
                        error = "Дата начала не может быть позже даты конца";
                    break;

                // Можно добавить валидацию для других свойств по необходимости
                // case nameof(SelectedFilterId):
                //     // Пример проверки, что это число (хотя TextBox уже привязан к int)
                //     break; 
            }
            // Важно: после валидации нужно обновить состояние команды
            // CommandManager.InvalidateRequerySuggested() вызывается WPF автоматически при изменении свойств,
            // но если валидация сложная, может потребоваться явный вызов
             if (StartDownloadCommand is RelayCommand rc) rc.NotifyCanExecuteChanged();
            
            return error;
        }
    }

    // --- НОВЫЕ Методы для UI Тем ---
    private void LoadUiThemesAndAccents()
    {
        // Загрузка базовых тем (Light, Dark)
        AvailableBaseUiThemes.Clear();
        // Исправленный способ получения базовых тем: фильтруем по Name == BaseColorScheme
        var baseThemes = ThemeManager.Current.Themes
                                     .Where(x => x.Name == x.BaseColorScheme) // Правильный способ определить базовую тему
                                     .Select(x => x.BaseColorScheme) // Берем имя базовой схемы (Light/Dark)
                                     .Distinct()
                                     .OrderBy(x => x);
        foreach (var baseThemeName in baseThemes)
        {
            AvailableBaseUiThemes.Add(baseThemeName);
        }

        // Загрузка цветов акцента (остается как было)
        AvailableAccentUiColors.Clear();
        foreach (var accent in ThemeManager.Current.Themes.GroupBy(x => x.ColorScheme).Select(x => x.Key).OrderBy(x => x))
        {
            AvailableAccentUiColors.Add(accent);
        }

        // Установка текущей темы (или дефолтной)
        try
        {
            var currentTheme = ThemeManager.Current.DetectTheme(Application.Current);
            if (currentTheme != null)
            {
                _selectedBaseUiTheme = currentTheme.BaseColorScheme;
                _selectedAccentUiColor = currentTheme.ColorScheme;
                 FileLogger.Log($"Обнаружена текущая UI тема: {currentTheme.Name}");
            }
            else
            {
                _selectedBaseUiTheme = "Light";
                _selectedAccentUiColor = "Blue";
                 FileLogger.Log("Текущая UI тема не обнаружена, установлены значения по умолчанию: Light.Blue");
            }
             // Уведомляем UI об изменениях начальных значений
            OnPropertyChanged(nameof(SelectedBaseUiTheme));
            OnPropertyChanged(nameof(SelectedAccentUiColor));
        }
        catch (Exception ex)
        {
            // Может возникнуть исключение, если Application.Current еще не доступен или null
            _selectedBaseUiTheme = "Light"; // Безопасные значения по умолчанию
            _selectedAccentUiColor = "Blue";
            FileLogger.Log($"Ошибка при определении темы MahApps: {ex.Message}. Установлены значения по умолчанию.");
            OnPropertyChanged(nameof(SelectedBaseUiTheme));
            OnPropertyChanged(nameof(SelectedAccentUiColor));
        }
    }

    private void ApplyUiTheme()
    {
        if (string.IsNullOrEmpty(SelectedBaseUiTheme) || string.IsNullOrEmpty(SelectedAccentUiColor))
        {
            FileLogger.Log("Применение UI темы: Не выбрана базовая тема или цвет акцента.");
            return;
        }

        try
        {
            ThemeManager.Current.ChangeTheme(Application.Current, SelectedBaseUiTheme, SelectedAccentUiColor);
            FileLogger.Log($"UI тема успешно изменена на: {SelectedBaseUiTheme}.{SelectedAccentUiColor}");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"Ошибка при смене UI темы на {SelectedBaseUiTheme}.{SelectedAccentUiColor}: {ex.Message}");
            // Можно добавить сообщение пользователю, если нужно
        }
    }
    // --- Конец новых методов для UI Тем ---

    // --- Метод для команды открытия папки из лога ---
    private bool CanOpenLogDirectory(LogMessage logMessage)
    {
        // Простая проверка, есть ли в сообщении что-то похожее на путь
        return logMessage != null && (logMessage.Message.Contains("\\") || Regex.IsMatch(logMessage.Message, @"[a-zA-Z]:\\"));
    }

    private void OpenLogDirectory(LogMessage logMessage)
    {
        if (logMessage == null) 
        {
            AddLogMessage("OpenLogDirectory: logMessage is null.", "Warning");
            return;
        }

        // Исправляем экранирование кавычек
        AddLogMessage($"OpenLogDirectory: Пытаемся извлечь путь из: \"{logMessage.Message}\"", "Info");

        // Улучшенное регулярное выражение: ищет пути в кавычках или без, локальные и UNC
        var match = Regex.Match(logMessage.Message, @"(?<path>(?:""|')?(?:[a-zA-Z]:\\(?:[^'""\r\n]*)|\\\\(?:[^'""\r\n]*))(?:""|')?)");

        if (match.Success)
        {
            // Исправляем Trim: '\'' для апострофа
            string potentialPath = match.Groups["path"].Value.Trim('"', '\'').TrimEnd('.', ',', ':', ';', ')', ' ');
            // Исправляем экранирование кавычек
            AddLogMessage($"OpenLogDirectory: Regex нашел совпадение: \"{potentialPath}\"", "Info");

            string directoryPath = null;

            try
            {
                // Проверяем, является ли сам путь существующей директорией
                if (Directory.Exists(potentialPath))
                {
                    directoryPath = potentialPath;
                    AddLogMessage($"OpenLogDirectory: Найденный путь является существующей директорией: {directoryPath}", "Info");
                    // Открываем директорию
                    AddLogMessage($"OpenLogDirectory: Запуск explorer.exe для директории: \"{directoryPath}\"", "Info");
                    Process.Start("explorer.exe", directoryPath);
                }
                // Если это не директория, проверяем, существует ли как файл
                else if (File.Exists(potentialPath))
                {
                    // Формируем аргументы для выделения файла
                    string arguments = $"/select,\"{potentialPath}\"";
                    AddLogMessage($"OpenLogDirectory: Найденный путь является файлом. Выделяем: {potentialPath}", "Info");
                    AddLogMessage($"OpenLogDirectory: Запуск explorer.exe с аргументами: {arguments}", "Info");
                    Process.Start("explorer.exe", arguments);
                }
                else
                {
                    // Исправляем экранирование кавычек
                    AddLogMessage($"OpenLogDirectory: Путь \"{potentialPath}\" не существует как файл или директория.", "Warning");
                }

                // Пытаемся открыть, если удалось определить директорию и она существует - ЛОГИКА ПЕРЕНЕСЕНА ВЫШЕ
                // if (!string.IsNullOrEmpty(directoryPath) && Directory.Exists(directoryPath))
                // { ... }
                // else if (!string.IsNullOrEmpty(directoryPath))
                // { ... }
            }
            catch (Exception ex)
            {
                // Исправляем экранирование кавычек
                AddLogMessage($"OpenLogDirectory: Ошибка при обработке пути \"{potentialPath}\" или открытии директории: {ex.ToString()}", "Error");
            }
        }
        else
        {
             AddLogMessage("OpenLogDirectory: Регулярное выражение не нашло совпадений пути в сообщении.", "Warning");
        }
    }
    // --- Конец метода команды ---

    // Добавляем определение для AvailableThemes
    private ObservableCollection<ThemeInfo> _availableThemes = new ObservableCollection<ThemeInfo>();
    public ObservableCollection<ThemeInfo> AvailableThemes
    {
        get => _availableThemes;
        private set => SetProperty(ref _availableThemes, value);
    }

    // --- НОВЫЕ МЕТОДЫ для статистики по датам ---
    private async Task InitializeDateStatisticsAsync(DataTable fileTable)
    {
        if (fileTable == null) return;
        AddLogMessage("InitializeDateStatisticsAsync: Расчет начальной статистики...");
        try
        {
            // Выполняем LINQ в фоновом потоке
            var countsByDateList = await Task.Run(() => 
            {
                return fileTable.AsEnumerable()
                    .Where(row => row["publishDate"] != DBNull.Value)
                    .GroupBy(row => DateTime.Parse(row["publishDate"].ToString()).Date)
                    .Select(g => new { Date = g.Key, Count = g.Count() })
                    .OrderBy(x => x.Date)
                    .ToList(); // Материализуем результат в фоновом потоке
            });

            // Обновляем словарь и коллекцию в UI потоке
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                FileCountsPerDate.Clear();
                _fileCountsDict.Clear();
                foreach (var item in countsByDateList)
                {
                    var newStat = new DailyFileCount { Date = item.Date, Count = item.Count, ProcessedCount = 0 };
                    FileCountsPerDate.Add(newStat);
                    _fileCountsDict.Add(item.Date, newStat);
                }
                 AddLogMessage($"InitializeDateStatisticsAsync: Статистика инициализирована для {FileCountsPerDate.Count} дат.");
            });
        }
        catch (Exception ex)
        {
             AddLogMessage($"InitializeDateStatisticsAsync: Ошибка: {ex.Message}", "Error");
        }
    }

    private async Task UpdateDateStatisticsAsync(DataTable fileTable)
    {
        if (fileTable == null) return;
         AddLogMessage("UpdateDateStatisticsAsync: Обновление статистики...");
         try
         {
             // Выполняем LINQ в фоновом потоке
             var currentCountsByDateDict = await Task.Run(() => 
             {
                 return fileTable.AsEnumerable()
                     .Where(row => row["publishDate"] != DBNull.Value)
                     .GroupBy(row => DateTime.Parse(row["publishDate"].ToString()).Date)
                     .ToDictionary(g => g.Key, g => g.Count()); // Сразу в словарь
             });

            // Обновляем словарь и коллекцию в UI потоке
             await Application.Current.Dispatcher.InvokeAsync(() =>
             {
                 foreach (var kvp in currentCountsByDateDict)
                 {
                     var date = kvp.Key;
                     var newCount = kvp.Value;

                     if (!_fileCountsDict.TryGetValue(date, out var existingStat))
                     {
                         // Новая дата
                         var newStat = new DailyFileCount { Date = date, Count = newCount, ProcessedCount = 0 };
                         FileCountsPerDate.Add(newStat); // Добавляем в ObservableCollection
                         _fileCountsDict.Add(date, newStat); // Добавляем в словарь
                         AddLogMessage($"UpdateDateStatisticsAsync: Добавлена дата {date:dd.MM.yyyy} ({newCount} файлов).");
                     }
                     else if (existingStat.Count < newCount)
                     {
                         // Существующая дата, но файлов стало больше
                         AddLogMessage($"UpdateDateStatisticsAsync: Обновлен счетчик для {date:dd.MM.yyyy}. Было: {existingStat.Count}, стало: {newCount}.");
                         existingStat.Count = newCount; // Обновляем свойство, UI обновится через INotifyPropertyChanged
                     }
                 }
                 // Сортировка, если нужна (пока убрана)
             });
              AddLogMessage($"UpdateDateStatisticsAsync: Обновление завершено.");
         }
         catch (Exception ex)
         {
             AddLogMessage($"UpdateDateStatisticsAsync: Ошибка: {ex.Message}", "Error");
         }
    }
    // --- Конец НОВЫХ МЕТОДОВ для статистики ---

    // --- Асинхронный метод загрузки баз ---
    private async Task LoadAvailableDatabasesAsync()
    {
        AddLogMessage($"LoadAvailableDatabasesAsync: Начало.");
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            AddLogMessage("LoadAvailableDatabasesAsync: Ошибка - _baseConnectionString пустая.", "Error");
            await Application.Current.Dispatcher.InvokeAsync(() => 
                SetProperty(ref _availableDatabases, new ObservableCollection<DatabaseInfo>(), nameof(AvailableDatabases)) // Обновляем UI
            ); 
            return;
        }

        try
        {
            var databases = new[] { "fcsNotification", "contract", "purchaseNotice", "requestQuotation" };
            var availableDbs = new List<DatabaseInfo>();
            
            foreach (var db in databases)
            {
                try
                {
                    var connectionString = _baseConnectionString + $";Initial Catalog={db};Connect Timeout=5"; // Короткий таймаут
                    using (var connection = new SqlConnection(connectionString))
                    {
                        await connection.OpenAsync(); // Асинхронное открытие
                        
                        string displayName;
                        switch (db)
                        {
                           // ... (логика switch) ...
                            case "fcsNotification": displayName = "Извещения 44 (fcsNotification)"; break;
                            case "contract": displayName = "Контракт (contract)"; break;
                            case "purchaseNotice": displayName = "Извещения 223 (purchaseNotice)"; break;
                            case "requestQuotation": displayName = "Запрос цен (requestQuotation)"; break;
                            default: 
                                displayName = db;
                                break; // <-- ДОБАВИТЬ ЭТОТ BREAK
                        }
                        availableDbs.Add(new DatabaseInfo { Name = db, DisplayName = displayName });
                        AddLogMessage($"LoadAvailableDatabasesAsync: База {displayName} доступна.", "Success");
                    }
                }
                catch (Exception ex)
                {
                    AddLogMessage($"LoadAvailableDatabasesAsync: База {db} недоступна: {ex.Message}", "Warning");
                }
            }

            // Обновляем коллекцию в UI потоке
            await Application.Current.Dispatcher.InvokeAsync(() => 
                 SetProperty(ref _availableDatabases, new ObservableCollection<DatabaseInfo>(availableDbs), nameof(AvailableDatabases))
            ); 
             AddLogMessage($"LoadAvailableDatabasesAsync: Загружено {availableDbs.Count} баз.");
        }
        catch (Exception ex)
        {
             AddLogMessage($"LoadAvailableDatabasesAsync: КРИТИЧЕСКАЯ ОШИБКА: {ex.Message}", "Error");
             await Application.Current.Dispatcher.InvokeAsync(() => 
                 SetProperty(ref _availableDatabases, new ObservableCollection<DatabaseInfo>(), nameof(AvailableDatabases))
             );
        }
    }

    // --- Асинхронный метод загрузки тем ---
    private async Task LoadAvailableThemesAsync()
    {
        AddLogMessage("LoadAvailableThemesAsync: Загрузка тем...");
        if (string.IsNullOrEmpty(_iacConnectionString))
        {
             AddLogMessage("LoadAvailableThemesAsync: Ошибка - строка IacConnection не загружена.", "Error");
             await Application.Current.Dispatcher.InvokeAsync(() => 
                SetProperty(ref _availableThemes, new ObservableCollection<ThemeInfo>(), nameof(AvailableThemes))
             ); 
             return;
        }

        try
        {
            var connectionString = _iacConnectionString;
            var themes = new List<ThemeInfo>();
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync(); // Асинхронное открытие
                using (var command = new SqlCommand("SELECT ThemeID, themeName FROM Theme", connection))
                {
                    using (var reader = await command.ExecuteReaderAsync()) // Асинхронное чтение
                    {
                        while (await reader.ReadAsync()) // Асинхронное чтение строки
                        {
                            themes.Add(new ThemeInfo
                            {
                                Id = reader.GetInt32(0),
                                Name = reader.GetString(1)
                            });
                        }
                    }
                }
            }
            // Обновляем коллекцию в UI потоке
            await Application.Current.Dispatcher.InvokeAsync(() =>
                 SetProperty(ref _availableThemes, new ObservableCollection<ThemeInfo>(themes), nameof(AvailableThemes))
            ); 
            AddLogMessage($"LoadAvailableThemesAsync: Загружено {themes.Count} тем.", "Success");
        }
        catch (Exception ex)
        {
            AddLogMessage($"LoadAvailableThemesAsync: Ошибка при загрузке тем: {ex.Message}", "Error");
            await Application.Current.Dispatcher.InvokeAsync(() => 
                SetProperty(ref _availableThemes, new ObservableCollection<ThemeInfo>(), nameof(AvailableThemes))
            );
        }
    }

    // Event handler for files archived by ArchiveService
    private void HandleExtractedFileArchived(object sender, FileArchivedEventArgs e)
    {
        // Run on UI thread if necessary, but logging can often be done directly
        // Application.Current.Dispatcher.Invoke(() => 
        // {
                AddLogMessage($"[Архивация содержимого] Файл '{Path.GetFileName(e.OriginalPath)}' зарегистрирован и перемещен в '{e.NewPath}' как '{e.NewFileName}'. MetaID: {e.DocumentMetadata?.documentMetaID}", "Success");
        // });
    }

    // Метод для обработки вложенных архивов
    private async Task ProcessNestedArchiveAsync(string archivePath, string extractionPath, CancellationToken token)
    {
        // Проверки пути и токена отмены
        if (string.IsNullOrEmpty(archivePath) || string.IsNullOrEmpty(extractionPath) || !File.Exists(archivePath))
        {
            AddLogMessage($"Ошибка: Некорректные параметры для обработки вложенного архива. Путь: {archivePath}", "Error");
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        string nestedExtractPath = Path.Combine(extractionPath, Path.GetFileNameWithoutExtension(archivePath));
        AddLogMessage($"Обработка вложенного архива: {Path.GetFileName(archivePath)} -> {nestedExtractPath}");

        try
        {
            // Создаем отдельную директорию для вложенного архива
            if (!Directory.Exists(nestedExtractPath))
            {
                Directory.CreateDirectory(nestedExtractPath);
            }

            // Распаковываем вложенный архив
            using (var archive = SharpCompress.Archives.ArchiveFactory.Open(archivePath))
            {
                var options = new SharpCompress.Common.ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true,
                    PreserveFileTime = true
                };

                int extractedFilesCount = 0;
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    if (token.IsCancellationRequested) break;

                    try
                    {
                        entry.WriteToDirectory(nestedExtractPath, options);
                        extractedFilesCount++;
                    }
                    catch (Exception ex)
                    {
                        AddLogMessage($"Ошибка при распаковке файла {entry.Key} из вложенного архива: {ex.Message}", "Warning");
                    }
                }

                AddLogMessage($"Вложенный архив распакован. Извлечено файлов: {extractedFilesCount}");
            }

            // Удаляем исходный вложенный архив, так как его содержимое распаковано
            File.Delete(archivePath);
            AddLogMessage($"Вложенный архив {Path.GetFileName(archivePath)} удален после распаковки");
        }
        catch (Exception ex)
        {
            AddLogMessage($"Ошибка при обработке вложенного архива {Path.GetFileName(archivePath)}: {ex.Message}", "Error");
        }
    }

    // Метод для сканирования и рекурсивной распаковки вложенных архивов
    private async Task ScanAndExtractNestedArchivesAsync(string rootExtractPath, CancellationToken token, int maxDepth = 2, int currentDepth = 0)
    {
        if (currentDepth >= maxDepth || token.IsCancellationRequested)
        {
            if (currentDepth >= maxDepth)
            {
                AddLogMessage($"Достигнута максимальная глубина рекурсии ({maxDepth}) для распаковки вложенных архивов", "Warning");
            }
            return;
        }

        try
        {
            // Ищем все архивы в директории
            var archiveExtensions = new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2" };
            var nestedArchives = Directory.GetFiles(rootExtractPath, "*.*", SearchOption.AllDirectories)
                                         .Where(f => archiveExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                                         .ToList();

            if (!nestedArchives.Any())
            {
                return; // Нет вложенных архивов
            }

            AddLogMessage($"Найдено вложенных архивов: {nestedArchives.Count} (глубина {currentDepth + 1}/{maxDepth})");

            // Обрабатываем каждый найденный архив
            foreach (var archivePath in nestedArchives)
            {
                if (token.IsCancellationRequested) break;

                string parentDir = Path.GetDirectoryName(archivePath);
                await ProcessNestedArchiveAsync(archivePath, parentDir, token);
            }

            // Рекурсивно сканируем распакованные директории на предмет новых архивов
            await ScanAndExtractNestedArchivesAsync(rootExtractPath, token, maxDepth, currentDepth + 1);
        }
        catch (Exception ex)
        {
            AddLogMessage($"Ошибка при сканировании вложенных архивов: {ex.Message}", "Error");
        }
    }
} 