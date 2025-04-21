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
using DownloaderApp.Infrastructure;
using Microsoft.Extensions.Configuration;
using FluentFTP;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using DownloaderApp.Models;
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
using Microsoft.Data.Sqlite;
using SharpCompress.Archives;
using SharpCompress.Common;
using System.IO.Compression;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using System.Data;
using DownloaderApp.Utilities;
using DownloaderApp.Services;
using DownloaderApp.Constants;
using Microsoft.Extensions.Options;
using NLog;
using MahApps.Metro.Controls;
using DownloaderApp.Infrastructure.Logging;

namespace FileDownloader.ViewModels;

public class DailyFileCount : INotifyPropertyChanged
{
    private int _processedCount;
    private int _count;
    private DateTime _date;

    public DateTime Date
    {
        get => _date;
        set => SetProperty(ref _date, value);
    }

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    public int ProcessedCount
    {
        get => _processedCount;
        set => SetProperty(ref _processedCount, value);
    }

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
}

enum CircuitBreakerState { Closed, Open, HalfOpen }

internal record FileMetadataRecord(
    int DocumentMetaID,
    string Url,
    DateTime PublishDate,
    string ComputerName,
    string DirectoryName,
    int DocumentMetaPathID,
    string PathDirectory,
    string FlDocument,
    string Ftp,
    string FileNameFtp,
    string FileName,
    string ExpName,
    string DocDescription,
    object UrlID
);

public class DownloaderViewModel : ObservableObject, IDataErrorInfo
{
    private DatabaseService _databaseService;
    private HttpClientService _httpClientService;
    private ConfigurationService _configurationService;
    private ArchiveService _archiveService;
    private Logger _logger;
    private readonly IFileLogger _fileLogger;

    private volatile int _adaptiveDelayMilliseconds = 0;

    private ApplicationSettings _currentSettings = new ApplicationSettings();
    public ApplicationSettings CurrentSettings
    {
        get => _currentSettings;
        private set => SetProperty(ref _currentSettings, value);
    }

    private FtpSettings _currentFtpSettings = new FtpSettings();
    public FtpSettings CurrentFtpSettings
    {
        get => _currentFtpSettings;
        private set => SetProperty(ref _currentFtpSettings, value);
    }

    private DatabaseInfo _selectedDatabase;
    public DatabaseInfo SelectedDatabase
    {
        get => _selectedDatabase;
        set
        {
            if (SetProperty(ref _selectedDatabase, value))
            {
                OnSelectedDatabaseChanged(value);
            }
        }
    }

    private async void OnSelectedDatabaseChanged(DatabaseInfo value)
    {
        await _fileLogger.LogInfoAsync($"SelectedDatabase изменена на: {(value != null ? value.DisplayName : "NULL")}");
        _ = LoadAvailableThemes();
        if (StartDownloadCommand is AsyncRelayCommand rc)
        {
            rc.NotifyCanExecuteChanged();
            await _fileLogger.LogInfoAsync("NotifyCanExecuteChanged вызван для StartDownloadCommand из SelectedDatabase");
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
                OnSelectedThemeChanged(value);
            }
        }
    }

    private async void OnSelectedThemeChanged(ThemeInfo value)
    {
        await _fileLogger.LogInfoAsync($"SelectedTheme изменена на: {(value != null ? value.Name : "NULL")}");
        if (StartDownloadCommand is AsyncRelayCommand rc)
        {
            rc.NotifyCanExecuteChanged();
            await _fileLogger.LogInfoAsync("NotifyCanExecuteChanged вызван для StartDownloadCommand из SelectedTheme");
        }
    }

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

    private int? _selectedThemeId;
    public int? SelectedThemeId
    {
        get => _selectedThemeId;
        set => SetProperty(ref _selectedThemeId, value);
    }

    private int _selectedSourceId = 0;
    public int SelectedSourceId
    {
        get => _selectedSourceId;
        set => SetProperty(ref _selectedSourceId, value);
    }

    private int _selectedFilterId = 0;
    public int SelectedFilterId
    {
        get => _selectedFilterId;
        set => SetProperty(ref _selectedFilterId, value);
    }

    private bool _checkProvError;
    public bool CheckProvError
    {
        get => _checkProvError;
        set => SetProperty(ref _checkProvError, value);
    }

    private bool _ignoreDownloadErrors;
    public bool IgnoreDownloadErrors
    {
        get => _ignoreDownloadErrors;
        set => SetProperty(ref _ignoreDownloadErrors, value);
    }

    private int _totalFiles;
    public int TotalFiles
    {
        get => _totalFiles;
        set => SetProperty(ref _totalFiles, value);
    }

    private int _processedFiles;
    public int ProcessedFiles
    {
        get => _processedFiles;
        set => SetProperty(ref _processedFiles, value);
    }

    private string _currentFileName = "";
    public string CurrentFileName
    {
        get => _currentFileName;
        set => SetProperty(ref _currentFileName, value);
    }

    private string _statusMessage = "Готов";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            SetProperty(ref _isDownloading, value);
        }
    }

    private ObservableCollection<LogMessage> _logMessages = new ObservableCollection<LogMessage>();
    public ObservableCollection<LogMessage> LogMessages => _logMessages;

    private ObservableCollection<LogMessage> _filteredLogMessages = new ObservableCollection<LogMessage>();
    public ObservableCollection<LogMessage> FilteredLogMessages => _filteredLogMessages;

    private ObservableCollection<LogFilterType> _logFilterTypes = new ObservableCollection<LogFilterType>();
    public ObservableCollection<LogFilterType> LogFilterTypes => _logFilterTypes;

    private LogFilterType _selectedLogFilterType;
    public LogFilterType SelectedLogFilterType
    {
        get => _selectedLogFilterType;
        set
        {
            if (SetProperty(ref _selectedLogFilterType, value))
            {
                _ = UpdateFilteredLogMessages();
                if (CopyLogToClipboardCommand is RelayCommand rc) rc.NotifyCanExecuteChanged();
            }
        }
    }

    public ICommand StartDownloadCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand CopyLogToClipboardCommand { get; }
    public ICommand OpenLogDirectoryCommand { get; }
    public ICommand OpenFileLocationCommand { get; }

    public ObservableCollection<DailyFileCount> FileCountsPerDate { get; } = new ObservableCollection<DailyFileCount>();

    private CancellationTokenSource _cancellationTokenSource;

    private string _baseConnectionString;
    private string _iacConnectionString;
    private string _serverOfficeConnectionString;

    private bool _updateFlag;
    public bool UpdateFlag
    {
        get => _updateFlag;
        set
        {
            if (SetProperty(ref _updateFlag, value))
            {
                if (value)
                {
                    // Если флаг установлен в true, автоматически запускаем загрузку
                    if (StartDownloadCommand is RelayCommand rc && rc.CanExecute(null))
                    {
                        rc.Execute(null);
                    }
                }
            }
        }
    }

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        set
        {
            double change = Math.Abs(value - _downloadProgress);
            if (change < 0.01 && value != 0 && value != 100)
            {
                return; // Слишком незначительное изменение, не обновляем UI
            }
            SetProperty(ref _downloadProgress, value);
        }
    }

    private string _currentFileDetails;
    public string CurrentFileDetails
    {
        get => _currentFileDetails;
        set => SetProperty(ref _currentFileDetails, value);
    }

    private string _downloadSpeed;
    public string DownloadSpeed
    {
        get => _downloadSpeed;
        set => SetProperty(ref _downloadSpeed, value);
    }

    private string _estimatedTimeRemaining;
    public string EstimatedTimeRemaining
    {
        get => _estimatedTimeRemaining;
        set => SetProperty(ref _estimatedTimeRemaining, value);
    }

    private DispatcherTimer _uiUpdateTimer;
    private readonly ConcurrentQueue<DateTime> _processedDatesSinceLastUpdate = new ConcurrentQueue<DateTime>();
    private long _lastProcessedCountForUI = 0;
    private const int UIUpdateIntervalMilliseconds = 1000;
    private long _processedFilesCounter = 0;

    private readonly ConcurrentQueue<LogMessage> _logMessageQueue = new ConcurrentQueue<LogMessage>();

    private string _lastProcessedFileName = null;

    private readonly Dictionary<DateTime, DailyFileCount> _fileCountsDict = new Dictionary<DateTime, DailyFileCount>();

    private volatile bool _isInitialized = false;
    private volatile bool _statisticsInitialized = false; // Добавляем флаг инициализации статистики

    private static readonly Random _random = new Random();

    // Добавляем определение для AvailableThemes
    private ObservableCollection<ThemeInfo> _availableThemes = new ObservableCollection<ThemeInfo>();
    public ObservableCollection<ThemeInfo> AvailableThemes
    {
        get => _availableThemes;
        private set => SetProperty(ref _availableThemes, value);
    }

    // --- Поля для Circuit Breaker ---
    private volatile CircuitBreakerState _breakerState = CircuitBreakerState.Closed;
    private DateTime _breakerOpenUntilUtc = DateTime.MinValue;
    private volatile int _consecutive429Failures = 0;
    private const int BreakerFailureThreshold = 5; // Порог срабатывания
    private readonly TimeSpan BreakerOpenDuration = TimeSpan.FromSeconds(30); // Время размыкания
    private readonly object _breakerLock = new object(); // Для синхронизации доступа к состоянию
    // --- Конец полей для Circuit Breaker ---

    private readonly string[] _databases = { "fcsNotification", "contract", "purchaseNotice", "requestQuotation" };

    private DownloaderViewModel(IFileLogger fileLogger)
    {
        // Добавляем лог в самое начало конструктора
        _ = fileLogger?.LogDebugAsync("DownloaderViewModel: Вход в конструктор."); // Лог Constructor-Start (fire-and-forget)

        _fileLogger = fileLogger ?? throw new ArgumentNullException(nameof(fileLogger));

        // Добавляем лог перед инициализацией команд
        _ = _fileLogger?.LogDebugAsync("DownloaderViewModel: Перед инициализацией команд."); // Лог Constructor-BeforeCommands (fire-and-forget)

        StartDownloadCommand = new AsyncRelayCommand(StartDownloadAsync, () => !_isDownloading && _isInitialized);
        CancelDownloadCommand = new RelayCommand(CancelDownload, () => _isDownloading);
        OpenSettingsCommand = new RelayCommand(OpenSettings, () => !_isDownloading);
        ClearLogCommand = new RelayCommand(ClearLog);
        CopyLogToClipboardCommand = new RelayCommand(CopyLogToClipboard);
        OpenLogDirectoryCommand = new RelayCommand(OpenLogDirectory);
        OpenFileLocationCommand = new RelayCommand<string>(OpenFileLocation, (filePath) => !string.IsNullOrEmpty(filePath) && File.Exists(filePath));

        // Добавляем лог перед InitializeUiUpdateTimer
        _ = _fileLogger?.LogDebugAsync("DownloaderViewModel: Перед InitializeUiUpdateTimer."); // Лог Constructor-BeforeTimerInit (fire-and-forget)

        InitializeUiUpdateTimer();

        // Добавляем лог в конец конструктора
        _ = _fileLogger?.LogDebugAsync("DownloaderViewModel: Выход из конструктора."); // Лог Constructor-End (fire-and-forget)
    }

    private async Task LoadConfigurationAndSettingsAsync()
    {
        try
        {
            _logger = LogManager.GetCurrentClassLogger();
            _configurationService = new ConfigurationService();
            // Передаем _fileLogger в конструктор HttpClientService
            _httpClientService = new HttpClientService(_fileLogger);
            _archiveService = new ArchiveService(_logger); // TODO: Возможно, сюда тоже нужен IFileLogger?

            // Получаем строки подключения напрямую
            _baseConnectionString = _configurationService.GetBaseConnectionString();
            _iacConnectionString = _configurationService.GetIacConnectionString();
            _serverOfficeConnectionString = _configurationService.GetServerOfficeConnectionString();

            if (string.IsNullOrEmpty(_baseConnectionString))
            {
                // Используем _fileLogger для логирования критической ошибки
                await _fileLogger.LogCriticalAsync("Base connection string was not loaded correctly.");
                throw new InvalidOperationException("Base connection string was not loaded correctly.");
            }

            // Передаем _fileLogger в конструктор DatabaseService
            _databaseService = new DatabaseService(_baseConnectionString, _fileLogger);
            
            // Получаем настройки напрямую
            CurrentSettings = _configurationService.GetApplicationSettings();
            CurrentFtpSettings = _configurationService.GetFtpSettings();

            // Инициализация фильтров логов и тем UI
            await InitializeLogFilterTypes(); // <-- Последнее сообщение в логе было отсюда
            await _fileLogger.LogDebugAsync("LoadConfigurationAndSettingsAsync: После InitializeLogFilterTypes."); // <-- Лог 1

            PopulateThemeSelectors(); // <-- Вызов синхронного метода
            await _fileLogger.LogDebugAsync("LoadConfigurationAndSettingsAsync: После PopulateThemeSelectors."); // <-- Лог 2
            
            // Применение тем UI
            await _fileLogger.LogDebugAsync("LoadConfigurationAndSettingsAsync: Перед установкой тем UI."); // <-- Лог 3
            SelectedBaseUiTheme = AvailableBaseUiThemes.Contains(CurrentSettings.BaseTheme)
                ? CurrentSettings.BaseTheme
                : AvailableBaseUiThemes.FirstOrDefault() ?? "Light";
                
            SelectedAccentUiColor = AvailableAccentUiColors.Contains(CurrentSettings.AccentColor)
                ? CurrentSettings.AccentColor
                : AvailableAccentUiColors.FirstOrDefault() ?? "Blue";
            await _fileLogger.LogDebugAsync("LoadConfigurationAndSettingsAsync: После установки тем UI."); // <-- Лог 4

            await _fileLogger.LogInfoAsync("Конфигурация успешно загружена");
        }
        catch (Exception ex)
        {
            await _fileLogger.LogErrorAsync("Ошибка при загрузке конфигурации", ex);
            throw;
        }
    }

    private async Task InitializeAsync()
    {
        await _fileLogger.LogDebugAsync("InitializeAsync: Вход в метод."); // <-- Лог 5
        try
        {
            await _fileLogger.LogInfoAsync("Начало инициализации");
            
            await _fileLogger.LogDebugAsync("InitializeAsync: Перед LoadAvailableDatabases."); // <-- Лог 6
            await LoadAvailableDatabases();
            await _fileLogger.LogDebugAsync("InitializeAsync: Перед LoadAvailableThemes."); // <-- Лог 7
            await LoadAvailableThemes();
            await _fileLogger.LogDebugAsync("InitializeAsync: После LoadAvailableThemes."); // <-- Лог 8
            
            StatusMessage = "Готов";
            _isInitialized = true;
            // Логируем успешную инициализацию и установку флага
            await _fileLogger.LogSuccessAsync($"InitializeAsync: Инициализация завершена успешно. _isInitialized = {_isInitialized}");
        }
        catch (Exception ex)
        {
            StatusMessage = "Ошибка инициализации";
            // Добавляем логирование в catch
            await _fileLogger.LogErrorAsync("InitializeAsync: Ошибка при инициализации", ex);
            // Оставляем throw, чтобы ошибка всплыла в SetDataContextAsync
            throw; 
        }
    }

    public static async Task<DownloaderViewModel> CreateAsync(IFileLogger fileLogger)
    {
        var viewModel = new DownloaderViewModel(fileLogger);
        await viewModel.LoadConfigurationAndSettingsAsync();
        await viewModel.InitializeAsync();
        return viewModel;
    }

    private async Task StartDownloadAsync()
    {
        // Используем Debug.WriteLine для проверки входа в метод
        System.Diagnostics.Debug.WriteLine("-------> StartDownloadAsync: Вход в метод <-------");
        
        // Логируем вход в метод и ключевые параметры
        await _fileLogger.LogInfoAsync("StartDownloadAsync: Вход в метод.");
        await _fileLogger.LogInfoAsync($"StartDownloadAsync: _isInitialized = {_isInitialized}, _isDownloading = {_isDownloading}");
        await _fileLogger.LogInfoAsync($"StartDownloadAsync: SelectedDatabase = {(SelectedDatabase == null ? "NULL" : SelectedDatabase.DisplayName)}, SelectedTheme = {(SelectedTheme == null ? "NULL" : SelectedTheme.Name)}");
        
        // Проверка базовых условий для старта
        if (SelectedDatabase == null || SelectedTheme == null)
        {
            AddLogMessage("Ошибка: Не выбрана база данных или тема для загрузки.", "Error");
            await _fileLogger.LogErrorAsync("StartDownloadAsync: Загрузка не начата - не выбрана база данных или тема.");
            // Возможно, стоит сбросить флаг IsDownloading, если он был установлен
             IsDownloading = false; 
             (StartDownloadCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
             (CancelDownloadCommand as RelayCommand)?.NotifyCanExecuteChanged();
             (OpenSettingsCommand as RelayCommand)?.NotifyCanExecuteChanged();
            return;
        }
        
        await _fileLogger.LogInfoAsync("Старт процесса загрузки файлов");
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        IsDownloading = true;
        (StartDownloadCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (CancelDownloadCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (OpenSettingsCommand as RelayCommand)?.NotifyCanExecuteChanged();

        var processedFileIdsInThisSession = new ConcurrentDictionary<int, bool>();

        string finalStatus = "Загрузка завершена.";
        string basePath = null;
        StatusMessage = "Инициализация...";
        LogMessages.Clear();
        FileCountsPerDate.Clear();
        _fileCountsDict.Clear();
        await _fileLogger.LogInfoAsync("Запуск процесса загрузки...");

        string databaseName = SelectedDatabase.Name;
        int themeId = SelectedTheme.Id;
        int srcID = SelectedSourceId;
        bool flProv = CheckProvError;
        DateTime dtB = BeginDate.Date;
        DateTime dtE = EndDate.Date.AddDays(1).AddTicks(-1);
        int filterId = SelectedFilterId;

        var semaphore = new SemaphoreSlim(CurrentSettings.MaxParallelDownloads);
        
        // --- Определение целевой строки подключения ---
        string targetDbConnectionString;
        if (string.Equals(databaseName, "fcsNotification", StringComparison.OrdinalIgnoreCase))
        {
            // Используем специальную строку ServerOfficeConnection для fcsNotification
            targetDbConnectionString = _serverOfficeConnectionString;
            await _fileLogger.LogInfoAsync($"Используется ServerOfficeConnection для базы {databaseName}.");
        }
        else
        {
            // Используем базовую строку и добавляем Initial Catalog для других баз
            var conStrBuilder = new SqlConnectionStringBuilder(_baseConnectionString) { InitialCatalog = databaseName };
            targetDbConnectionString = conStrBuilder.ConnectionString;
             await _fileLogger.LogInfoAsync($"Используется BaseConnectionString + InitialCatalog={databaseName}.");
       }

        if (string.IsNullOrEmpty(targetDbConnectionString))
        {
            AddLogMessage($"Ошибка: Не удалось определить строку подключения для базы данных {databaseName}", "Error");
            await _fileLogger.LogErrorAsync($"StartDownloadAsync: Не удалось определить строку подключения для базы {databaseName}.");
            IsDownloading = false;
             (StartDownloadCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
             (CancelDownloadCommand as RelayCommand)?.NotifyCanExecuteChanged();
             (OpenSettingsCommand as RelayCommand)?.NotifyCanExecuteChanged();
             return;
        }
        
        string iacConnectionString = _iacConnectionString;

        TimeSpan checkInterval = TimeSpan.FromMinutes(30);
        bool firstCheck = true;

        _processedFilesCounter = 0;
        while (_processedDatesSinceLastUpdate.TryDequeue(out _)) { }
        _lastProcessedCountForUI = 0;
        ProcessedFiles = 0;
        _statisticsInitialized = false; // Сбрасываем флаг при старте загрузки
        _uiUpdateTimer.Start();

        try
        {
            while (DateTime.Now <= dtE && !token.IsCancellationRequested)
            {
                if (!firstCheck)
                {
                    await _fileLogger.LogInfoAsync($"Ожидание {checkInterval.TotalMinutes} минут перед следующей проверкой новых файлов...");
                    AddLogMessage($"Ожидание {checkInterval.TotalMinutes} минут перед следующей проверкой новых файлов...");
                    await Task.Delay(checkInterval, token);
                }
                firstCheck = false;

                if (token.IsCancellationRequested) break;

                AddLogMessage($"{(processedFileIdsInThisSession.IsEmpty ? "Первичная" : "Повторная")} проверка файлов за период с {dtB:dd.MM.yyyy} по {dtE:dd.MM.yyyy HH:mm:ss}...");
                await _fileLogger.LogInfoAsync($"{(processedFileIdsInThisSession.IsEmpty ? "Первичная" : "Повторная")} проверка файлов за период с {dtB:dd.MM.yyyy} по {dtE:dd.MM.yyyy HH:mm:ss}...");
                StatusMessage = "Получение списка файлов...";

                DataTable dtTab = null;
                int currentTotalFiles = 0;

                try
                {
                    // --- ВСЕГДА используем динамическую строку для FetchFileListAsync ---
                    var fetchConStrBuilder = new SqlConnectionStringBuilder(_baseConnectionString) { InitialCatalog = databaseName };
                    string fetchConnectionString = fetchConStrBuilder.ConnectionString;
                    await _fileLogger.LogDebugAsync($"Используется строка для FetchFileListAsync: {fetchConnectionString.Substring(0, fetchConnectionString.IndexOf(';'))}...");
                    
                    using (SqlConnection conBase = new SqlConnection(fetchConnectionString)) // Используем fetchConnectionString
                    {
                        await conBase.OpenAsync(token);
                        // Используем fetchConnectionString для вызова процедуры
                        dtTab = await FetchFileListAsync(fetchConnectionString, dtB, dtE, themeId, srcID, token);
                        currentTotalFiles = dtTab?.Rows.Count ?? 0;
                        await _fileLogger.LogDebugAsync($"StartDownloadAsync: FetchFileListAsync вернул {currentTotalFiles} строк.");

                        // Инициализируем статистику ПЕРВЫЙ раз, когда найдены файлы
                        if (!_statisticsInitialized && currentTotalFiles > 0)
                        {
                            TotalFiles = currentTotalFiles;
                            await _fileLogger.LogInfoAsync($"StartDownloadAsync: TotalFiles установлен в {TotalFiles}");
                            AddLogMessage($"Обнаружено {TotalFiles} файлов для обработки за период.");
                            await _fileLogger.LogInfoAsync($"Обнаружено {TotalFiles} файлов для обработки за период.");
                            if (basePath == null && srcID == 0 && dtTab.Rows.Count > 0)
                            {
                                try { /* ... код определения basePath ... */ }
                                catch (Exception pathEx) { AddLogMessage($"Ошибка при определении базового пути: {pathEx.Message}"); }
                            }
                            await InitializeDateStatisticsAsync(dtTab); // Вызов инициализации
                            _statisticsInitialized = true; // Устанавливаем флаг, что статистика инициализирована
                            await _fileLogger.LogInfoAsync("StartDownloadAsync: Статистика по датам инициализирована.");
                        }
                        // Если статистика уже инициализирована, но пришли новые данные (например, в режиме мониторинга)
                        // Можно добавить вызов UpdateDateStatisticsAsync(dtTab); здесь, если это требуется.
                        // else if (_statisticsInitialized && currentTotalFiles > 0) {
                        //     await UpdateDateStatisticsAsync(dtTab);
                        // }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AddLogMessage($"Критическая ошибка при получении списка файлов: {ex.Message}. Проверка будет повторена через {checkInterval.TotalMinutes} минут.", "Error");
                    await _fileLogger.LogErrorAsync($"Критическая ошибка при получении списка файлов: {ex.Message}. Проверка будет повторена через {checkInterval.TotalMinutes} минут.");
                    StatusMessage = "Ошибка получения списка файлов.";
                    continue;
                }

                if (dtTab == null || dtTab.Rows.Count == 0)
                {
                    AddLogMessage("Не найдено файлов для обработки в указанном диапазоне или произошла ошибка при получении списка.");
                    await _fileLogger.LogInfoAsync("Не найдено файлов для обработки в указанном диапазоне или произошла ошибка при получении списка.");
                    continue;
                }

                var filesToProcess = dtTab.AsEnumerable()
                                        .Where(row => !processedFileIdsInThisSession.ContainsKey(Convert.ToInt32(row["documentMetaID"])))
                                        .ToList();

                if (!filesToProcess.Any())
                {
                    AddLogMessage("Новых файлов для обработки не найдено в этой проверке.");
                    await _fileLogger.LogInfoAsync("Новых файлов для обработки не найдено в этой проверке.");
                    continue;
                }

                AddLogMessage($"Найдено {filesToProcess.Count} новых файлов для обработки в этой проверке.");
                await _fileLogger.LogInfoAsync($"Найдено {filesToProcess.Count} новых файлов для обработки в этой проверке.");
                StatusMessage = $"Обработка {filesToProcess.Count} новых файлов...";

                var tasks = new List<Task>();
                var progressReporter = new Progress<double>(progress => DownloadProgress = progress);
                
                // Удаляем логику пакетного обновления
                // var batchUpdateIds = new List<int>();
                // var batchUpdateLock = new object();
                // const int batchUpdateThreshold = 20;

                foreach (DataRow row in filesToProcess)
                {
                    if (token.IsCancellationRequested) break;

                    await semaphore.WaitAsync(token);

                    tasks.Add(Task.Run(async () =>
                    {
                        int documentMetaId = Convert.ToInt32(row["documentMetaID"]);
                        DateTime publishDate = DateTime.Now;
                        
                        try
                        {
                            if (token.IsCancellationRequested) return;

                            publishDate = DateTime.Parse(row["publishDate"].ToString()).Date;

                            // ProcessFileAsync теперь не возвращает bool
                            await ProcessFileAsync(row, targetDbConnectionString, iacConnectionString, databaseName, srcID, flProv, themeId, token, progressReporter);

                            processedFileIdsInThisSession.TryAdd(documentMetaId, true);

                            Interlocked.Increment(ref _processedFilesCounter);
                            _processedDatesSinceLastUpdate.Enqueue(publishDate);
                        }
                        catch (OperationCanceledException)
                        {
                            AddLogMessage($"Обработка файла (ID: {documentMetaId}) отменена.", "Warning");
                            await _fileLogger.LogWarningAsync($"Обработка файла (ID: {documentMetaId}) отменена.");
                        }
                        catch (Exception ex)
                        {
                            string originalFileName = row.Table.Columns.Contains("fileName") ? row["fileName"].ToString() : $"ID: {documentMetaId}";
                            AddLogMessage($"Ошибка при обработке файла '{originalFileName}': {ex.Message}", "Error");
                            await _fileLogger.LogErrorAsync($"Ошибка при обработке файла '{originalFileName}': {ex.Message}");
                            if (!IgnoreDownloadErrors)
                            {
                                AddLogMessage($"Обработка файла '{originalFileName}' пропущена из-за ошибки.", "Warning");
                                await _fileLogger.LogWarningAsync($"Обработка файла '{originalFileName}' пропущена из-за ошибки.");
                            }
                            else
                            {
                                AddLogMessage($"Ошибка обработки файла '{originalFileName}' проигнорирована согласно настройкам.", "Warning");
                                await _fileLogger.LogWarningAsync($"Ошибка обработки файла '{originalFileName}' проигнорирована согласно настройкам.");
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, token));
                }

                try
                {
                    await Task.WhenAll(tasks);
                    
                    // Удаляем финальное пакетное обновление
                    // if (batchUpdateIds.Count > 0)
                    // { ... }
                }
                catch (OperationCanceledException)
                {
                    AddLogMessage("Операция отменена пользователем во время обработки файлов.", "Warning");
                    AddLogMessage("Операция отменена пользователем во время обработки файлов.", "Warning");
                    await _fileLogger.LogWarningAsync("Операция отменена пользователем во время обработки файлов.");
                    throw;
                }

                AddLogMessage("Обработка текущей пачки новых файлов завершена.");
                await _fileLogger.LogInfoAsync($"Обработка текущей пачки новых файлов завершена.");

            }

            if (token.IsCancellationRequested)
            {
                finalStatus = "Загрузка отменена пользователем.";
                AddLogMessage(finalStatus, "Warning");
                await _fileLogger.LogWarningAsync(finalStatus);
            }
            else if (DateTime.Now > dtE)
            {
                finalStatus = $"Мониторинг завершен. Достигнута конечная дата: {dtE:dd.MM.yyyy HH:mm:ss}.";
                AddLogMessage(finalStatus);
            }
            else
            {
                finalStatus = "Загрузка завершена.";
            }
            AddLogMessage($"Всего обработано файлов в этом сеансе: {_processedFilesCounter}");
            await _fileLogger.LogInfoAsync($"Всего обработано файлов в этом сеансе: {_processedFilesCounter}");

        }
        catch (OperationCanceledException)
        {
            finalStatus = "Загрузка отменена.";
            AddLogMessage(finalStatus, "Warning");
        }
        catch (Exception ex)
        {
            finalStatus = $"Ошибка во время загрузки: {ex.Message}";
            AddLogMessage($"Критическая ошибка: {ex.ToString()}", "Error");
            await _fileLogger.LogErrorAsync($"Критическая ошибка: {ex.ToString()}");
        }
        finally
        {
            _uiUpdateTimer?.Stop(); // Добавляем проверку на null
            // Явно указываем fire-and-forget для вызова async Task метода
            _ = UpdateUiFromTimerTick();

            IsDownloading = false;
            if (_processedFilesCounter > 0 && !string.IsNullOrEmpty(basePath))
            {
                finalStatus += $" Файлы сохранены в: {basePath}...";
                AddLogMessage($"Успешно обработанные файлы сохранены в подпапки директории: {basePath}");
            }
            else if (_processedFilesCounter > 0 && srcID != 0)
            {
                AddLogMessage($"Обработка файлов для источника ID={srcID} завершена.");
            }

            StatusMessage = finalStatus;
            CurrentFileName = "";
            DownloadProgress = 0;
            DownloadSpeed = "";
            EstimatedTimeRemaining = "";

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            (StartDownloadCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            (CancelDownloadCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (OpenSettingsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }
        await _fileLogger.LogInfoAsync($"Завершение процесса загрузки файлов. Итог: {StatusMessage}");
    }

    private async void UiUpdateTimer_Tick(object sender, EventArgs e)
    {
        // Логируем срабатывание таймера
        await _fileLogger.LogDebugAsync("UiUpdateTimer_Tick: Таймер сработал.");
        await UpdateUiFromTimerTick();
    }

    private async Task UpdateUiFromTimerTick()
    {
        // Логируем вход в метод обновления UI
        await _fileLogger.LogDebugAsync("UpdateUiFromTimerTick: Вход в метод.");
        long currentTotalProcessed = Interlocked.Read(ref _processedFilesCounter);
        // Логируем текущее значение счетчика
        await _fileLogger.LogDebugAsync($"UpdateUiFromTimerTick: _processedFilesCounter = {currentTotalProcessed}");
        if (currentTotalProcessed != _lastProcessedCountForUI)
        {
            ProcessedFiles = (int)currentTotalProcessed;
            _lastProcessedCountForUI = currentTotalProcessed;
        }

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
            foreach (var kvp in datesToUpdate)
            {
                var dateKey = kvp.Key; // Используем отдельную переменную для ключа даты
                var countToAdd = kvp.Value;
                // Логируем попытку обновления для даты
                await _fileLogger.LogDebugAsync($"UpdateUiFromTimerTick: Попытка обновить дату {dateKey:dd.MM.yyyy}, добавить {countToAdd} шт.");
                
                if (_fileCountsDict.TryGetValue(dateKey, out var dailyStat))
                {
                    // Логируем текущее значение и добавляемое
                    await _fileLogger.LogDebugAsync($"UpdateUiFromTimerTick: Найдена статистика для {dateKey:dd.MM.yyyy}. Текущий ProcessedCount={dailyStat.ProcessedCount}. Добавляем {countToAdd}.");
                    dailyStat.ProcessedCount += countToAdd;
                }
                else
                {
                    // Логируем, что статистика не найдена в словаре
                    await _fileLogger.LogWarningAsync($"UpdateUiFromTimerTick: Не найдена статистика в СЛОВАРЕ для даты {dateKey:dd.MM.yyyy}. Попытка найти в списке...");
                    AddLogMessage($"UpdateUiFromTimerTick: Не найдена статистика в словаре для даты {dateKey:dd.MM.yyyy}.", "Warning");
                    // await _fileLogger.LogInfoAsync($"UpdateUiFromTimerTick: Не найдена статистика в словаре для даты {kvp.Key:dd.MM.yyyy}."); // Дублирующее сообщение
                    var statFromList = FileCountsPerDate.FirstOrDefault(d => d.Date == dateKey);
                    if (statFromList != null)
                    {
                        await _fileLogger.LogDebugAsync($"UpdateUiFromTimerTick: Найдена статистика в СПИСКЕ для {dateKey:dd.MM.yyyy}. Текущий ProcessedCount={statFromList.ProcessedCount}. Добавляем {countToAdd}.");
                        statFromList.ProcessedCount += countToAdd;
                    }
                    else
                    {
                         await _fileLogger.LogErrorAsync($"UpdateUiFromTimerTick: Статистика для {dateKey:dd.MM.yyyy} НЕ НАЙДЕНА ни в словаре, ни в списке!");
                    }
                }
            }
        }

        string latestFileName = _lastProcessedFileName;
        if (latestFileName != null && latestFileName != CurrentFileName)
        {
            CurrentFileName = latestFileName;
        }

        var logsToAdd = new List<LogMessage>();
        while (_logMessageQueue.TryDequeue(out var logMessage))
        {
            logsToAdd.Add(logMessage);
        }

        if (logsToAdd.Count > 0)
        {
            const int maxLogMessages = 1000;
            int currentCount = LogMessages.Count;
            int itemsToAddCount = logsToAdd.Count;
            int itemsToRemove = currentCount + itemsToAddCount - maxLogMessages;

            if (itemsToRemove > 0)
            {
                itemsToRemove = Math.Min(itemsToRemove, currentCount);
                for (int i = 0; i < itemsToRemove; i++)
                {
                    if (LogMessages.Count > 0)
                    {
                        LogMessages.RemoveAt(0);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            foreach (var log in logsToAdd)
            {
                LogMessages.Add(log);
            }

            // await UpdateFilteredLogMessages(); // Удаляем этот вызов, т.к. он вызывается ниже
            // Вместо полного обновления FilteredLogMessages, добавляем только новые подходящие сообщения
            if (_selectedLogFilterType == null || _selectedLogFilterType.Type == "All")
            {
                foreach (var log in logsToAdd)
                {
                    FilteredLogMessages.Add(log);
                }
            }
            else
            {
                foreach (var log in logsToAdd.Where(l => l.Type == _selectedLogFilterType.Type))
                {
                    FilteredLogMessages.Add(log);
                }
            }
            
            // Ограничиваем размер FilteredLogMessages, если он превышает maxLogMessages
            while (FilteredLogMessages.Count > maxLogMessages)
            {
                FilteredLogMessages.RemoveAt(0);
            }
        }
    }

    private async Task<DownloadResult> WebGetAsync(string url, string tempFilePath, CancellationToken token, IProgress<double> progress)
    {
        // Используем HttpClientService вместо прямой реализации
        // Возвращаемый тип HttpClientService.DownloadFileAsync - DownloaderApp.Models.DownloadResult
        // Тип возвращаемого значения этого метода теперь тоже DownloaderApp.Models.DownloadResult
        return await _httpClientService.DownloadFileAsync(url, tempFilePath, token, progress);
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
                await _fileLogger.LogInfoAsync($"Подключение к FTP: {ftpSettings.FtpHost}:{ftpSettings.FtpPort}...");
                await ftpClient.Connect(token);
                AddLogMessage("FTP подключение установлено.");
                await _fileLogger.LogInfoAsync("FTP подключение установлено.");

                string remotePath = remoteFileName; // TODO: Уточнить логику пути
                AddLogMessage($"Загрузка на FTP: {localFilePath} -> {remotePath}");
                await _fileLogger.LogInfoAsync($"Загрузка на FTP: {localFilePath} -> {remotePath}");

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
                await _fileLogger.LogInfoAsync("FTP загрузка завершена.");
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
            finally { if (ftpClient.IsConnected) { await ftpClient.Disconnect(token); AddLogMessage("FTP соединение закрыто."); await _fileLogger.LogInfoAsync("FTP соединение закрыто."); } }
        }
    }

    private void AddLogMessage(string message, string type = "Info", string filePath = null)
    {
        var logMessage = new LogMessage
        {
            Message = message,
            Type = type,
            Timestamp = DateTime.Now,
            FilePath = filePath // Присваиваем путь к файлу
        };

        // Просто добавляем сообщение в потокобезопасную очередь
        _logMessageQueue.Enqueue(logMessage);
    }

    private async Task UpdateFilteredLogMessages()
    {
        // Этот метод теперь вызывается ТОЛЬКО при смене фильтра
        try
        {
            if (LogMessages == null)
            {
                AddLogMessage("UpdateFilteredLogMessages: LogMessages is null!", "Error");
                await _fileLogger.LogErrorAsync("UpdateFilteredLogMessages: LogMessages is null!");
                return;
            }
            
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IEnumerable<LogMessage> messagesToShow;
                if (SelectedLogFilterType == null || SelectedLogFilterType.Type == "All")
                {
                    messagesToShow = LogMessages.ToList(); // Копируем, чтобы избежать проблем с многопоточностью
                }
                else
                {
                    messagesToShow = LogMessages.Where(m => m.Type == SelectedLogFilterType.Type).ToList();
                }
                
                FilteredLogMessages.Clear();
                foreach (var message in messagesToShow)
                {
                    FilteredLogMessages.Add(message);
                }
            });
        }
        catch (Exception ex)
        {
            // Логируем ошибку самого метода обновления
            Debug.WriteLine($"[ERROR] Ошибка в UpdateFilteredLogMessages: {ex}"); 
            // Можно также добавить запись в основной лог, если нужно, но осторожно
            // await _fileLogger?.LogErrorAsync("Ошибка в UpdateFilteredLogMessages", ex); 
        }
    }

    private async Task InitializeLogFilterTypes()
    {
        LogFilterTypes.Clear();
        LogFilterTypes.Add(new LogFilterType { Name = "All", DisplayName = "Все сообщения", Type = "All" });
        LogFilterTypes.Add(new LogFilterType { Name = "Error", DisplayName = "Ошибки", Type = "Error" });
        LogFilterTypes.Add(new LogFilterType { Name = "Warning", DisplayName = "Предупреждения", Type = "Warning" });
        LogFilterTypes.Add(new LogFilterType { Name = "Info", DisplayName = "Информация", Type = "Info" });
        LogFilterTypes.Add(new LogFilterType { Name = "Success", DisplayName = "Успешно", Type = "Success" });
        SelectedLogFilterType = LogFilterTypes.First();
        AddLogMessage("InitializeLogFilterTypes: Фильтры инициализированы.", "Info");
        await _fileLogger.LogInfoAsync("InitializeLogFilterTypes: Фильтры инициализированы.");
    }

    // --- Свойства для баз данных и тем ---
    private ObservableCollection<DatabaseInfo> _availableDatabases = new ObservableCollection<DatabaseInfo>();
    public ObservableCollection<DatabaseInfo> AvailableDatabases
    {
        get => _availableDatabases;
        private set => SetProperty(ref _availableDatabases, value);
    }

    // ... existing code ...

    private async Task LoadAvailableDatabases()
    {
        AddLogMessage($"LoadAvailableDatabases: Начало. Проверка _baseConnectionString.", "Info");
        await _fileLogger.LogInfoAsync($"LoadAvailableDatabases: Начало. Проверка _baseConnectionString.");
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            AddLogMessage("LoadAvailableDatabases: Ошибка - _baseConnectionString пустая или null.", "Error");
            await _fileLogger.LogErrorAsync("LoadAvailableDatabases: Ошибка - _baseConnectionString пустая или null.");
            return;
        }
        AddLogMessage($"LoadAvailableDatabases: _baseConnectionString = '{_baseConnectionString}'.", "Info");
        await _fileLogger.LogInfoAsync($"LoadAvailableDatabases: _baseConnectionString = '{_baseConnectionString}'.");
        var databases = new[] { "fcsNotification", "contract", "purchaseNotice", "requestQuotation" };
        AddLogMessage($"LoadAvailableDatabases: Начало цикла проверки {databases.Length} баз данных.", "Info");
        await _fileLogger.LogInfoAsync($"LoadAvailableDatabases: Начало цикла проверки {databases.Length} баз данных.");
        try
        {
            var availableDbs = new List<DatabaseInfo>();
            foreach (var db in databases)
            {
                try
                {
                    var connectionString = _baseConnectionString + $";Initial Catalog={db}";
                    AddLogMessage($"LoadAvailableDatabases: Попытка подключения к {db} ({connectionString})", "Info");
                    await _fileLogger.LogInfoAsync($"LoadAvailableDatabases: Попытка подключения к {db} ({connectionString})");
                    using (var connection = new SqlConnection(connectionString))
                    {
                        connection.Open();
                        string displayName;
                        switch (db)
                        {
                            case "fcsNotification": displayName = "Извещения 44 (fcsNotification)"; break;
                            case "contract": displayName = "Контракт (contract)"; break;
                            case "purchaseNotice": displayName = "Извещения 223 (purchaseNotice)"; break;
                            case "requestQuotation": displayName = "Запрос цен (requestQuotation)"; break;
                            default: displayName = db; break;
                        }
                        availableDbs.Add(new DatabaseInfo { Name = db, DisplayName = displayName });
                        AddLogMessage($"LoadAvailableDatabases: База данных {displayName} доступна.", "Info");
                        await _fileLogger.LogInfoAsync($"LoadAvailableDatabases: База данных {displayName} доступна.");
                        AddLogMessage($"LoadAvailableDatabases: База данных {displayName} доступна.", "Success");
                        await _fileLogger.LogSuccessAsync($"LoadAvailableDatabases: База данных {displayName} доступна.");
                    }
                }
                catch (Exception ex)
                {
                    AddLogMessage($"LoadAvailableDatabases: База данных {db} недоступна. Ошибка: {ex.GetType().Name} - {ex.Message}", "Warning");
                    await _fileLogger.LogErrorAsync($"LoadAvailableDatabases: База данных {db} недоступна. Ошибка: {ex.GetType().Name} - {ex.Message}");
                    await _fileLogger.LogErrorAsync($"LoadAvailableDatabases: База данных {db} недоступна. Ошибка: {ex.GetType().Name} - {ex.Message}");
                }
            }
            AddLogMessage($"LoadAvailableDatabases: Цикл проверки баз данных завершен. Найдено доступных: {availableDbs.Count}.", "Info");
            await _fileLogger.LogInfoAsync($"LoadAvailableDatabases: Цикл проверки баз данных завершен. Найдено доступных: {availableDbs.Count}.");
            AvailableDatabases = new ObservableCollection<DatabaseInfo>(availableDbs);
            AddLogMessage("LoadAvailableDatabases: Загрузка списка доступных баз данных завершена.", "Info");
            await _fileLogger.LogSuccessAsync("LoadAvailableDatabases: Загрузка списка доступных баз данных завершена.");
        }
        catch (Exception ex)
        {
            AddLogMessage($"LoadAvailableDatabases: КРИТИЧЕСКАЯ ОШИБКА: {ex.Message}", "Error");
            await _fileLogger.LogErrorAsync($"LoadAvailableDatabases: КРИТИЧЕСКАЯ ОШИБКА: {ex.Message}");
            if (ex.InnerException != null)
            {
                AddLogMessage($"LoadAvailableDatabases: Внутренняя ошибка: {ex.InnerException.Message}", "Error");
                await _fileLogger.LogErrorAsync($"LoadAvailableDatabases: Внутренняя ошибка: {ex.InnerException.Message}");
            }
            AvailableDatabases.Clear();
        }
        AddLogMessage($"LoadAvailableDatabases: Завершение метода.", "Info");
        await _fileLogger.LogInfoAsync($"LoadAvailableDatabases: Завершение метода.");
    }

    // Метод для загрузки списка тем из базы IAC (zakupkiweb)
    private async Task LoadAvailableThemes()
    {
        AddLogMessage("LoadAvailableThemes: Загрузка тем из базы IAC (zakupkiweb)...", "Info");
        await _fileLogger.LogInfoAsync("LoadAvailableThemes: Загрузка тем из базы IAC (zakupkiweb)...");
        if (string.IsNullOrEmpty(_iacConnectionString))
        {
            AddLogMessage("LoadAvailableThemes: Ошибка - строка подключения IacConnection не загружена.", "Error");
            SetProperty(ref _availableThemes, new ObservableCollection<ThemeInfo>(), nameof(AvailableThemes));
            await _fileLogger.LogErrorAsync("LoadAvailableThemes: Ошибка - строка подключения IacConnection не загружена.");
            return;
        }
        AddLogMessage($"LoadAvailableThemes: Используется строка подключения _iacConnectionString='{_iacConnectionString}'", "Info");
        await _fileLogger.LogInfoAsync($"LoadAvailableThemes: Используется строка подключения _iacConnectionString='{_iacConnectionString}'");

        try
        {
            var connectionString = _iacConnectionString;
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand("SELECT ThemeID, themeName FROM Theme", connection))
                {
                    using (var reader = command.ExecuteReader())
                    {
                        var themes = new List<ThemeInfo>();
                        while (reader.Read())
                        {
                            themes.Add(new ThemeInfo
                            {
                                Id = reader.GetInt32(0),
                                Name = reader.GetString(1)
                            });
                        }
                        SetProperty(ref _availableThemes, new ObservableCollection<ThemeInfo>(themes), nameof(AvailableThemes));
                        AddLogMessage($"LoadAvailableThemes: Загружено {themes.Count} тем из IAC.", "Success");
                        await _fileLogger.LogSuccessAsync($"LoadAvailableThemes: Загружено {themes.Count} тем из IAC.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AddLogMessage($"LoadAvailableThemes: Ошибка при загрузке тем из IAC: {ex.Message}", "Error");
            await _fileLogger.LogErrorAsync($"LoadAvailableThemes: Ошибка при загрузке тем из IAC: {ex.Message}");
            SetProperty(ref _availableThemes, new ObservableCollection<ThemeInfo>(), nameof(AvailableThemes));
        }
        AddLogMessage("LoadAvailableThemes: Завершение метода.", "Info");
        await _fileLogger.LogInfoAsync("LoadAvailableThemes: Завершение метода.");
    }

    private void CancelDownload()
    {
        if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
        {
            AddLogMessage("Запрос на отмену операции...");
            _cancellationTokenSource.Cancel();
        }
    }

    private async Task<DataTable> FetchFileListAsync(string connectionString, DateTime dtB, DateTime dtE, int themeId, int srcID, CancellationToken token)
    {
        DataTable dtTab = new DataTable();
        using (SqlConnection conBase = new SqlConnection(connectionString))
        {
            await conBase.OpenAsync(token);
            using (SqlCommand cmd = new SqlCommand("documentMetaDownloadList", conBase))
            {
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.Parameters.AddWithValue("@themeID", themeId);
                cmd.Parameters.AddWithValue("@dtB", dtB);
                cmd.Parameters.AddWithValue("@dtE", dtE);
                cmd.Parameters.AddWithValue("@srcID", srcID); // Используем переданный srcID

                using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token))
                {
                    dtTab.Load(reader);
                }
            }
        }
        return dtTab;
    }

    private async Task ProcessFileAsync(DataRow row, string targetDbConnectionString, string iacConnectionString, string databaseName, int srcID, bool flProv, int themeId, CancellationToken token, IProgress<double> progress)
    {
        // --- Извлечение данных, ДОСТУПНЫХ из documentMetaDownloadList --- 
        int documentMetaID = GetValueOrDefault<int>(row, "documentMetaID"); 
        string url = GetValueOrDefault<string>(row, "url");
        DateTime publishDate = GetValueOrDefault<DateTime>(row, "publishDate", DateTime.MinValue); 
        string computerName = GetValueOrDefault<string>(row, "computerName");
        string directoryName = GetValueOrDefault<string>(row, "directoryName");
        string originalFileName = GetValueOrDefault<string>(row, "fileName");
        string expName = GetValueOrDefault<string>(row, "expName"); // Для имени файла
        string docDescriptionFromList = GetValueOrDefault<string>(row, "docDescription"); // Описание из списка (может отличаться от полного)

        bool shouldUpdateUI = _processedFilesCounter % 5 == 0; 

        string pathDirectory = GetValueOrDefault<string>(row, "PathDirectory");
        if (string.IsNullOrWhiteSpace(pathDirectory))
        {
            AddLogMessage($"Ошибка: PathDirectory отсутствует для файла '{originalFileName}' (ID: {documentMetaID})", "Error");
            await _fileLogger.LogErrorAsync($"PathDirectory отсутствует для файла '{originalFileName}' (ID: {documentMetaID})");
            return;
        }
string fileDocument = Path.Combine(pathDirectory, originalFileName);

        try // Основной try для ProcessFileAsync
        {
            if (flProv == false)
            {
                string dirOfFile = Path.GetDirectoryName(fileDocument);
                if (!string.IsNullOrEmpty(dirOfFile))
                {
                    Directory.CreateDirectory(dirOfFile);
                }
                else
                {
                    AddLogMessage($"Предупреждение: не удалось определить директорию для создания для файла {fileDocument}");
                    return; // Возвращаем false, если директория не определена
                }


                if (File.Exists(fileDocument))
                {
                    AddLogMessage($"Удаление существующего файла: {fileDocument}");
                    File.Delete(fileDocument);
                }

                if (shouldUpdateUI)
                {
                    AddLogMessage($"Скачивание: {url} -> {fileDocument}");
                }

                long fileSize = 0;
                DownloadResult downloadResult = null;
                bool downloadSucceeded = false;

                try // Внутренний try для скачивания
                {
                    if (_breakerState == CircuitBreakerState.Open)
                    {
                        if (DateTime.UtcNow < _breakerOpenUntilUtc)
                        {
                            throw new Exception($"Circuit Breaker is Open until {_breakerOpenUntilUtc}. Skipping download for '{originalFileName}'.");
                        }
                        else
                        {
                            lock (_breakerLock)
                            {
                                if (_breakerState == CircuitBreakerState.Open)
                                {
                                    _breakerState = CircuitBreakerState.HalfOpen;
                                    AddLogMessage("Circuit Breaker переходит в состояние Half-Open.");
                                }
                            }
                        }
                    }
                    int currentAdaptiveDelay = _adaptiveDelayMilliseconds;
                    if (currentAdaptiveDelay > 0)
                    {
                        AddLogMessage($"Применяется адаптивная задержка: {currentAdaptiveDelay} мс");
                        await _fileLogger.LogInfoAsync($"Применяется адаптивная задержка: {currentAdaptiveDelay} мс");
                        await Task.Delay(currentAdaptiveDelay, token);
                    }

                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        token.ThrowIfCancellationRequested();

                        if (attempt > 1 || shouldUpdateUI)
                        {
                            AddLogMessage($"Попытка скачивания #{attempt} для: {originalFileName}");
                            await _fileLogger.LogInfoAsync($"Попытка скачивания #{attempt} для: {originalFileName}");
                        }

                        downloadResult = await WebGetAsync(url, fileDocument, token, progress);

                        if (downloadResult.Success)
                        {
                            fileSize = downloadResult.ActualSize; // Получаем размер скачанного файла
                            downloadSucceeded = true;

                            if (shouldUpdateUI)
                            {
                                AddLogMessage($"Файл '{originalFileName}' скачан успешно (попытка {attempt}), размер: {fileSize} байт.");
                                await _fileLogger.LogInfoAsync($"Файл '{originalFileName}' скачан успешно (попытка {attempt}), размер: {fileSize} байт.");
                            }

                            Interlocked.Exchange(ref _consecutive429Failures, 0);

                            if (_breakerState == CircuitBreakerState.HalfOpen)
                            {
                                lock (_breakerLock)
                                {
                                    if (_breakerState == CircuitBreakerState.HalfOpen)
                                    {
                                        _breakerState = CircuitBreakerState.Closed;
                                        AddLogMessage("Circuit Breaker ЗАМКНУТ после успешной попытки в Half-Open.");
                                    }
                                }
                            }

                            currentAdaptiveDelay = _adaptiveDelayMilliseconds;
                            if (currentAdaptiveDelay > 0)
                            {
                                int newAdaptiveDelay = Math.Max(0, currentAdaptiveDelay - 500);
                                Interlocked.CompareExchange(ref _adaptiveDelayMilliseconds, newAdaptiveDelay, currentAdaptiveDelay);
                                AddLogMessage($"Уменьшена адаптивная задержка до: {newAdaptiveDelay} мс");
                            }
                            break;
                        }
                        else if (downloadResult.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
                        {
                            int delaySeconds = 1;
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
                            delaySeconds *= 2; // Увеличиваем *нашу* экспоненциальную задержку на случай, если Retry-After не было
                        }
                        else // Успех или другая ошибка (или последняя попытка 429)
                        {
                            // Успех уже обработан в первом if. Здесь только ошибки.
                            AddLogMessage($"Не удалось скачать файл '{originalFileName}' после {attempt} попыток. Ошибка: {downloadResult.ErrorMessage}");

                            // Если мы были в HalfOpen и получили ошибку (любую), снова размыкаем предохранитель
                            if (_breakerState == CircuitBreakerState.HalfOpen)
                            {
                                lock (_breakerLock)
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

                    if (!downloadSucceeded)
                    {
                        throw new Exception($"Не удалось скачать файл '{originalFileName}' после 3 попыток. Последняя ошибка: {downloadResult?.ErrorMessage ?? "Неизвестная ошибка"}");
                    }
                    // --- УСПЕШНОЕ СКАЧИВАНИЕ ---

                    Interlocked.Increment(ref _processedFilesCounter);
                    _processedDatesSinceLastUpdate.Enqueue(publishDate); // Используем publishDate для статистики
                    _lastProcessedFileName = originalFileName;

                    bool skipSuccessUpdate = false;

                    // --- Обработка архивов (если нужно) --- 
                    // ... (логика обработки архивов, если включена) ...
                    // if (isArchive) { ... } 
                    // else // Если это не архив (обычный файл)
                    {
                        await _fileLogger.LogDebugAsync($"DEBUG: Файл '{originalFileName}' (ID: {documentMetaID}) не является архивом. Попытка вставки метаданных.");
                        try
                        {
                            // --- ЗАПОЛНЕНИЕ МОДЕЛИ AttachmentModel ---
                            var attachment = new AttachmentModel
                            {

                            };
                        }
                        catch (Exception ex)
                        {
                        }

                    // Обновление статуса в БД
                    if (!skipSuccessUpdate)
                        {
                            int idToUpdateFlag = documentMetaID; // Используем documentMetaID для обновления флага
                            await _fileLogger.LogDebugAsync($"DEBUG: Флаг isProcessed будет обновлен для ID: {idToUpdateFlag}. skipSuccessUpdate={skipSuccessUpdate}.");
                            await _databaseService.UpdateDownloadFlagAsync(targetDbConnectionString, idToUpdateFlag, token);

                            if (shouldUpdateUI)
                            {
                                AddLogMessage($"Файл '{originalFileName}' (ID: {idToUpdateFlag}) успешно обработан.", "Success", fileDocument);
                                await _fileLogger.LogInfoAsync($"Файл '{originalFileName}' (ID: {idToUpdateFlag}) успешно обработан.");
                            }
                        }

                        return;
                    }
                }
                catch (Exception downloadEx) { throw; }
            }
            else // Если flProv == true
            {
                // ... (логика для flProv == true) ...
                return; 
            }
        }
        catch (Exception ex) // Внешний catch для ProcessFileAsync
        {
           // ... (логирование основной ошибки) ...
            if (ex is OperationCanceledException) throw;
            if (!IgnoreDownloadErrors) throw; // Пробрасываем, если не игнорируем
            return; // Иначе просто выходим
        }
    }

    // --- НОВЫЕ Методы для статистики по датам ---
    private async Task InitializeDateStatisticsAsync(DataTable fileTable)
    {
        // Добавлено логирование входа в метод
        await _fileLogger.LogInfoAsync("InitializeDateStatisticsAsync: Вход в метод.");
        if (fileTable == null)
        {
            // Логируем случай с null таблицей
            await _fileLogger.LogWarningAsync("InitializeDateStatisticsAsync: Входной fileTable равен null. Статистика не будет инициализирована.");
            return;
        }
        // Логируем количество строк во входной таблице
        await _fileLogger.LogInfoAsync($"InitializeDateStatisticsAsync: Входной fileTable содержит {fileTable.Rows.Count} строк.");

        AddLogMessage("InitializeDateStatisticsAsync: Расчет начальной статистики...");
        // await _fileLogger.LogInfoAsync("InitializeDateStatisticsAsync: Расчет начальной статистики..."); // Дублирующее сообщение
        try
        {
            var countsByDateList = await Task.Run(() =>
            {
                return fileTable.AsEnumerable()
                    .Where(row => row["publishDate"] != DBNull.Value && DateTime.TryParse(row["publishDate"].ToString(), out _))
                    .GroupBy(row => DateTime.Parse(row["publishDate"].ToString()).Date)
                    .Select(g => new { Date = g.Key, Count = g.Count() })
                    .OrderBy(x => x.Date)
                    .ToList();
            });
            
            // Логируем количество сгруппированных дат ПЕРЕД обновлением UI
            await _fileLogger.LogInfoAsync($"InitializeDateStatisticsAsync: Найдено {countsByDateList.Count} уникальных дат для статистики.");

            // Добавляем async к лямбда-выражению
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                // Логируем начало очистки и заполнения
                await _fileLogger.LogInfoAsync("InitializeDateStatisticsAsync (Dispatcher): Начало очистки и заполнения FileCountsPerDate и _fileCountsDict.");
                FileCountsPerDate.Clear();
                _fileCountsDict.Clear();
                foreach (var item in countsByDateList)
                {
                    var newStat = new DailyFileCount { Date = item.Date, Count = item.Count, ProcessedCount = 0 };
                    FileCountsPerDate.Add(newStat);
                    _fileCountsDict.Add(item.Date, newStat);
                    // Можно добавить логирование каждой добавляемой даты, если нужно (закомментировано для краткости)
                    // await _fileLogger.LogDebugAsync($"InitializeDateStatisticsAsync (Dispatcher): Добавлена дата {item.Date:dd.MM.yyyy} с {item.Count} файлами.");
                }
                // Логируем количество инициализированных дат ПОСЛЕ заполнения
                await _fileLogger.LogInfoAsync($"InitializeDateStatisticsAsync (Dispatcher): Заполнено {_fileCountsDict.Count} дат в словаре и {FileCountsPerDate.Count} в коллекции.");
                AddLogMessage($"InitializeDateStatisticsAsync: Статистика инициализирована для {FileCountsPerDate.Count} дат.");
            });
        }
        catch (Exception ex)
        {
            AddLogMessage($"InitializeDateStatisticsAsync: Ошибка: {ex.Message}", "Error");
            // Улучшено логирование ошибки
            await _fileLogger.LogErrorAsync("InitializeDateStatisticsAsync: Ошибка при расчете или обновлении статистики", ex);
        }
        // await _fileLogger.LogInfoAsync($"InitializeDateStatisticsAsync: Статистика инициализирована для {FileCountsPerDate.Count} дат."); // Дублирующее сообщение из Dispatcher
    }

    private async Task UpdateDateStatisticsAsync(DataTable fileTable)
    {
        // Реализация этого метода уже была добавлена ранее
        // Оставляем её как есть или добавляем, если отсутствует
        if (fileTable == null) return;
        AddLogMessage("UpdateDateStatisticsAsync: Обновление статистики...");
        await _fileLogger.LogInfoAsync("UpdateDateStatisticsAsync: Обновление статистики...");
        try
        {
            var currentCountsByDateDict = await Task.Run(() =>
            {
                return fileTable.AsEnumerable()
                   .Where(row => row["publishDate"] != DBNull.Value)
                   .GroupBy(row => DateTime.Parse(row["publishDate"].ToString()).Date)
                   .ToDictionary(g => g.Key, g => g.Count());
            });
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var kvp in currentCountsByDateDict)
                {
                    var date = kvp.Key;
                    var newCount = kvp.Value;
                    if (!_fileCountsDict.TryGetValue(date, out var existingStat))
                    {
                        var newStat = new DailyFileCount { Date = date, Count = newCount, ProcessedCount = 0 };
                        FileCountsPerDate.Add(newStat);
                        _fileCountsDict.Add(date, newStat);
                        AddLogMessage($"UpdateDateStatisticsAsync: Добавлена дата {date:dd.MM.yyyy} ({newCount} файлов).");
                    }
                    else if (existingStat.Count < newCount)
                    {
                        AddLogMessage($"UpdateDateStatisticsAsync: Обновлен счетчик для {date:dd.MM.yyyy}. Было: {existingStat.Count}, стало: {newCount}.");
                        existingStat.Count = newCount;
                        AddLogMessage($"UpdateDateStatisticsAsync: Обновлен счетчик для {date:dd.MM.yyyy}. Было: {existingStat.Count}, стало: {newCount}.");
                    }
                }
            });
            AddLogMessage($"UpdateDateStatisticsAsync: Обновление завершено.");
            await _fileLogger.LogInfoAsync($"UpdateDateStatisticsAsync: Обновление завершено.");
        }
        catch (Exception ex)
        {
            AddLogMessage($"UpdateDateStatisticsAsync: Ошибка: {ex.Message}", "Error");
            await _fileLogger.LogErrorAsync($"UpdateDateStatisticsAsync: Ошибка: {ex.Message}");
        }
    }
    // --- Конец НОВЫХ МЕТОДОВ для статистики ---

    // --- Асинхронный метод загрузки баз ---
    private async Task LoadAvailableDatabasesAsync()
    {
        await _fileLogger.LogInfoAsync($"LoadAvailableDatabasesAsync: Начало.");
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            await _fileLogger.LogErrorAsync("LoadAvailableDatabasesAsync: Ошибка - _baseConnectionString пустая.");
            await Application.Current.Dispatcher.InvokeAsync(() =>
                SetProperty(ref _availableDatabases, new ObservableCollection<DatabaseInfo>(), nameof(AvailableDatabases))
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
                    var connectionString = _baseConnectionString + $";Initial Catalog={db};Connect Timeout=5";
                    using (var connection = new SqlConnection(connectionString))
                    {
                        await connection.OpenAsync();
                        string displayName;
                        switch (db)
                        {
                            case "fcsNotification": displayName = "Извещения 44 (fcsNotification)"; break;
                            case "contract": displayName = "Контракт (contract)"; break;
                            case "purchaseNotice": displayName = "Извещения 223 (purchaseNotice)"; break;
                            case "requestQuotation": displayName = "Запрос цен (requestQuotation)"; break;
                            default: displayName = db; break;
                        }
                        availableDbs.Add(new DatabaseInfo { Name = db, DisplayName = displayName });
                        AddLogMessage($"LoadAvailableDatabasesAsync: База {displayName} доступна.", "Info");
                        await _fileLogger.LogInfoAsync($"LoadAvailableDatabasesAsync: База {displayName} доступна.");
                        AddLogMessage($"LoadAvailableDatabasesAsync: База данных {displayName} доступна.", "Success"); // Изменено
                        await _fileLogger.LogSuccessAsync($"LoadAvailableDatabasesAsync: База данных {displayName} доступна.");
                    }
                }
                catch (Exception ex)
                {
                    await _fileLogger.LogWarningAsync($"LoadAvailableDatabasesAsync: База {db} недоступна: {ex.Message}");
                }
            }
            await Application.Current.Dispatcher.InvokeAsync(() =>
                 SetProperty(ref _availableDatabases, new ObservableCollection<DatabaseInfo>(availableDbs), nameof(AvailableDatabases))
            );
            AddLogMessage($"LoadAvailableDatabasesAsync: Загружено {availableDbs.Count} баз.", "Info");
            await _fileLogger.LogInfoAsync($"LoadAvailableDatabasesAsync: Загружено {availableDbs.Count} баз.");
        }
        catch (Exception ex)
        {
            await _fileLogger.LogCriticalAsync($"LoadAvailableDatabasesAsync: КРИТИЧЕСКАЯ ОШИБКА: {ex.Message}");
            await Application.Current.Dispatcher.InvokeAsync(() =>
                SetProperty(ref _availableDatabases, new ObservableCollection<DatabaseInfo>(), nameof(AvailableDatabases))
            );
        }
    }

    // --- Асинхронный метод загрузки тем ---
    private async Task LoadAvailableThemesAsync()
    {
        await _fileLogger.LogInfoAsync("LoadAvailableThemesAsync: Загрузка тем...");
        if (string.IsNullOrEmpty(_iacConnectionString))
        {
            await _fileLogger.LogErrorAsync("LoadAvailableThemesAsync: Ошибка - строка IacConnection не загружена.");
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
                await connection.OpenAsync();
                using (var command = new SqlCommand("SELECT ThemeID, themeName FROM Theme", connection))
                {
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
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
            await Application.Current.Dispatcher.InvokeAsync(() =>
                 SetProperty(ref _availableThemes, new ObservableCollection<ThemeInfo>(themes), nameof(AvailableThemes))
            );
            AddLogMessage($"LoadAvailableThemesAsync: Загружено {themes.Count} тем.", "Success");
            await _fileLogger.LogSuccessAsync($"LoadAvailableThemesAsync: Загружено {themes.Count} тем.");
        }
        catch (Exception ex)
        {
            AddLogMessage($"LoadAvailableThemesAsync: Ошибка при загрузке тем: {ex.Message}", "Error");
            await _fileLogger.LogErrorAsync($"LoadAvailableThemesAsync: Ошибка при загрузке тем: {ex.Message}");
            await Application.Current.Dispatcher.InvokeAsync(() =>
                SetProperty(ref _availableThemes, new ObservableCollection<ThemeInfo>(), nameof(AvailableThemes))
            );
        }
    }

    // --- Реализация IDataErrorInfo ---
    public string Error => null;

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
                    if (SelectedTheme == null)
                        error = "Необходимо выбрать тему";
                    break;
                case nameof(BeginDate):
                case nameof(EndDate):
                    if (BeginDate > EndDate)
                        error = "Дата начала не может быть позже даты конца";
                    break;
            }
            if (StartDownloadCommand is AsyncRelayCommand rc) rc.NotifyCanExecuteChanged();
            return error;
        }
    }

    // --- Методы для команд ---
    private void ClearLog()
    {
        LogMessages.Clear();
        FilteredLogMessages.Clear();
        // Возможно, стоит добавить лог-сообщение об очистке
        AddLogMessage("Лог очищен.", "Info");
    }

    private void CopyLogToClipboard()
    {
        // TODO: Реализовать копирование логов (FilteredLogMessages) в буфер обмена
        AddLogMessage("Функция копирования лога пока не реализована.", "Warning");
        // Примерная реализация:
        // var logs = string.Join(Environment.NewLine, FilteredLogMessages.Select(m => $"[{m.Timestamp:HH:mm:ss}] [{m.Type}] {m.Message}"));
        // Clipboard.SetText(logs);
    }

    private void OpenLogDirectory()
    {
        // TODO: Реализовать открытие папки с логами (_fileLogger должен знать путь)
        AddLogMessage("Функция открытия папки логов пока не реализована.", "Warning");
        // Примерная реализация (требует доступа к пути из _fileLogger):
        // string logPath = _fileLogger.GetLogDirectory(); 
        // if (!string.IsNullOrEmpty(logPath) && Directory.Exists(logPath))
        // {
        //     Process.Start("explorer.exe", logPath);
        // }
        // else
        // {
        //     AddLogMessage("Не удалось определить путь к папке логов.", "Error");
        // }
    }

    private void OpenFileLocation(string filePath)
    {
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            catch (Exception ex)
            {
                AddLogMessage($"Ошибка при открытии папки с файлом: {ex.Message}", "Error");
            }
        }
        else
        {
            AddLogMessage("Не удалось открыть папку с файлом: файл не найден.", "Warning");
        }
    }
    // --- Конец методов для команд ---

    // --- Методы для UI и Таймера ---
    private void PopulateThemeSelectors()
    {
        // TODO: Заполнить AvailableBaseUiThemes и AvailableAccentUiColors
        // Пример:
        // AvailableBaseUiThemes.Clear();
        // ThemeManager.Current.Themes.GroupBy(x => x.BaseColorScheme).Select(x => x.Key).ToList().ForEach(AvailableBaseUiThemes.Add);
        // AvailableAccentUiColors.Clear();
        // ThemeManager.Current.Themes.Select(x => x.ColorScheme).Distinct().ToList().ForEach(AvailableAccentUiColors.Add);
        _fileLogger?.LogDebugAsync("Метод PopulateThemeSelectors вызван (заглушка).");
    }

    private void ApplyUiTheme()
    {
        // TODO: Применить выбранную тему через ThemeManager
        // Пример:
        // if (!string.IsNullOrEmpty(SelectedBaseUiTheme) && !string.IsNullOrEmpty(SelectedAccentUiColor))
        // {
        //     ThemeManager.Current.ChangeTheme(Application.Current, $"{SelectedBaseUiTheme}.{SelectedAccentUiColor}");
        // }
         _fileLogger?.LogDebugAsync($"Метод ApplyUiTheme вызван для {SelectedBaseUiTheme}.{SelectedAccentUiColor} (заглушка).");
   }

    private void InitializeUiUpdateTimer()
    {
        // Инициализируем таймер полностью (заглушка)
        _uiUpdateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(UIUpdateIntervalMilliseconds) // Используем константу
        };
        _uiUpdateTimer.Tick += UiUpdateTimer_Tick; // Подписываемся на существующий обработчик
         
        _fileLogger?.LogDebugAsync("Метод InitializeUiUpdateTimer вызван (заглушка).");
   }

    private void OpenSettings()
    {
        // TODO: Открыть окно настроек
        // Пример:
        // var settingsWindow = new SettingsWindow();
        // settingsWindow.Owner = Application.Current.MainWindow;
        // // Возможно, передать текущие настройки в ViewModel окна настроек
        // settingsWindow.ShowDialog();
        AddLogMessage("Функция открытия настроек пока не реализована.", "Warning");
    }
    // --- Конец методов для UI и Таймера ---

    #region DataRow Helper Methods

    private static T GetValueOrDefault<T>(DataRow row, string columnName, T defaultValue = default)
    {
        if (row.Table.Columns.Contains(columnName) && row[columnName] != DBNull.Value)
        {
            try
            {
                // Попытка прямого приведения или конвертации
                return (T)Convert.ChangeType(row[columnName], typeof(T));
            }
            catch (InvalidCastException)
            {
                // Если прямой каст не удался, попробуем через строку (для некоторых случаев)
                try
                {
                    return (T)Convert.ChangeType(row[columnName].ToString(), typeof(T));
                }
                catch { return defaultValue; } // Возвращаем дефолт, если ничего не помогло
            }
            catch { return defaultValue; } // Другие ошибки конвертации
        }
        return defaultValue;
    }

    private static T? GetNullableValue<T>(DataRow row, string columnName) where T : struct
    {
        if (row.Table.Columns.Contains(columnName) && row[columnName] != DBNull.Value)
        {
            try
            {
                // Попытка прямого приведения или конвертации
                return (T)Convert.ChangeType(row[columnName], typeof(T));
            }
             catch (InvalidCastException)
            {
                // Если прямой каст не удался, попробуем через строку
                try
                {
                     return (T)Convert.ChangeType(row[columnName].ToString(), typeof(T));
                }
                catch { return null; }
            }
            catch { return null; } // Другие ошибки конвертации
        }
        return null;
    }

    #endregion
}
