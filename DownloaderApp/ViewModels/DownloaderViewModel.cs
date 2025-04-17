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
using DownloaderApp.Models;
using Microsoft.Extensions.Options;
using NLog;
using MahApps.Metro.Controls;

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
    private readonly DatabaseService _databaseService;
    private readonly HttpClientService _httpClientService;
    private readonly ConfigurationService _configurationService;
    private readonly ArchiveService _archiveService;
    private readonly Logger _logger;

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
                AddLogMessage($"SelectedDatabase изменена на: {(value != null ? value.DisplayName : "NULL")}", "Info");
                LoadAvailableThemes();
                if (StartDownloadCommand is AsyncRelayCommand rc)
                {
                    rc.NotifyCanExecuteChanged();
                    AddLogMessage("NotifyCanExecuteChanged вызван для StartDownloadCommand из SelectedDatabase", "Info");
                }
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
                AddLogMessage($"SelectedTheme изменена на: {(value != null ? value.Name : "NULL")}", "Info");
                if (StartDownloadCommand is AsyncRelayCommand rc)
                {
                    rc.NotifyCanExecuteChanged();
                    AddLogMessage("NotifyCanExecuteChanged вызван для StartDownloadCommand из SelectedTheme", "Info");
                }
            }
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
                UpdateFilteredLogMessages();
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
    private ConcurrentQueue<DateTime> _processedDatesSinceLastUpdate = new ConcurrentQueue<DateTime>();
    private long _lastProcessedCountForUI = 0; 
    private const int UIUpdateIntervalMilliseconds = 1000; 
    private long _processedFilesCounter = 0;

    private readonly ConcurrentQueue<LogMessage> _logMessageQueue = new ConcurrentQueue<LogMessage>();

    private string _lastProcessedFileName = null;

    private readonly Dictionary<DateTime, DailyFileCount> _fileCountsDict = new Dictionary<DateTime, DailyFileCount>();

    private volatile bool _isInitialized = false;

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

    public DownloaderViewModel()
    {
        try
        {
            AddLogMessage("=== СТАРТ КОНСТРУКТОРА ===", "Info");
            // Инициализация служб
            _logger = LogManager.GetCurrentClassLogger();
            _configurationService = new ConfigurationService();
            _httpClientService = new HttpClientService();
            _archiveService = new ArchiveService(_logger);

            // Загружаем конфигурацию СНАЧАЛА, чтобы получить строки подключения
            LoadConfigurationAndSettings();
            FileLogger.Log("Конструктор DownloaderViewModel: LoadConfigurationAndSettings завершен");
            AddLogMessage("Конфигурация загружена", "Info");

            // ТЕПЕРЬ инициализируем DatabaseService с загруженной строкой подключения
            if (string.IsNullOrEmpty(_baseConnectionString))
            {
                 throw new InvalidOperationException("Base connection string was not loaded correctly.");
            }
            _databaseService = new DatabaseService(_baseConnectionString); // Принимает строку подключения

            // Инициализация команд
            StartDownloadCommand = new AsyncRelayCommand(StartDownloadAsync, CanStartDownload);
            AddLogMessage($"StartDownloadCommand создана как {StartDownloadCommand.GetType().FullName}", "Info");
            
            CancelDownloadCommand = new RelayCommand(CancelDownload, CanCancelDownload);
            OpenSettingsCommand = new RelayCommand(OpenSettingsWindow);
            ClearLogCommand = new RelayCommand(ClearLog, CanClearLog);
            CopyLogToClipboardCommand = new RelayCommand(CopyLogToClipboard, CanCopyLog);
            OpenLogDirectoryCommand = new RelayCommand(OpenLogDirectory);
            AddLogMessage("Команды инициализированы", "Info");

            StatusMessage = "Инициализация...";
            FileLogger.Log("Конструктор DownloaderViewModel: Завершен");

            InitializeUiUpdateTimer();
            InitializeLogFilterTypes();
            AddLogMessage("Таймеры и фильтры инициализированы", "Info");

            // Запускаем асинхронную инициализацию
            _ = InitializeAsync();

            // Инициализация коллекций
            PopulateThemeSelectors(); // Заполняем списки тем
            AddLogMessage("=== УСТАНОВКА ТЕМ ПО УМОЛЧАНИЮ ===", "Info");
            // Устанавливаем значения по умолчанию из настроек или первые доступные
            SelectedBaseUiTheme = AvailableBaseUiThemes.Contains(CurrentSettings.BaseTheme)
                                    ? CurrentSettings.BaseTheme
                                    : AvailableBaseUiThemes.FirstOrDefault() ?? "Light"; // Значение по умолчанию, если список пуст
            SelectedAccentUiColor = AvailableAccentUiColors.Contains(CurrentSettings.AccentColor)
                                    ? CurrentSettings.AccentColor
                                    : AvailableAccentUiColors.FirstOrDefault() ?? "Blue"; // Значение по умолчанию, если список пуст
            AddLogMessage($"Установлены темы UI: {SelectedBaseUiTheme}.{SelectedAccentUiColor}", "Info");
            AddLogMessage("=== КОНЕЦ КОНСТРУКТОРА ===", "Info");
        }
        catch (Exception ex)
        {
            StatusMessage = "Ошибка инициализации";
            AddLogMessage($"Критическая ошибка в конструкторе: {ex.Message}", "Error");
            FileLogger.Log($"Критическая ошибка в конструкторе DownloaderViewModel: {ex}");
        }
    }

    private void InitializeUiUpdateTimer()
    {
        _uiUpdateTimer = new DispatcherTimer(DispatcherPriority.Background);
        _uiUpdateTimer.Interval = TimeSpan.FromMilliseconds(UIUpdateIntervalMilliseconds);
        _uiUpdateTimer.Tick += UiUpdateTimer_Tick;
    }

    private async Task StartDownloadAsync()
    {
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
        AddLogMessage("Запуск процесса загрузки...");

        string databaseName = SelectedDatabase.Name;
        int themeId = SelectedTheme.Id;
        int srcID = SelectedSourceId;
        bool flProv = CheckProvError;
        DateTime dtB = BeginDate.Date;
        DateTime dtE = EndDate.Date.AddDays(1).AddTicks(-1);
        int filterId = SelectedFilterId;

        var semaphore = new SemaphoreSlim(CurrentSettings.MaxParallelDownloads);
        var conStrBuilder = new SqlConnectionStringBuilder(_baseConnectionString) { InitialCatalog = databaseName };
        string targetDbConnectionString = conStrBuilder.ConnectionString;
        string iacConnectionString = _iacConnectionString;

        TimeSpan checkInterval = TimeSpan.FromMinutes(30);
        bool firstCheck = true;

        _processedFilesCounter = 0;
        _processedDatesSinceLastUpdate = new ConcurrentQueue<DateTime>(); 
        _lastProcessedCountForUI = 0; 
        ProcessedFiles = 0;
        _uiUpdateTimer.Start();

        try
        {
            while (DateTime.Now <= dtE && !token.IsCancellationRequested)
            {
                if (!firstCheck)
                {
                    AddLogMessage($"Ожидание {checkInterval.TotalMinutes} минут перед следующей проверкой новых файлов...");
                    await Task.Delay(checkInterval, token);
                }
                firstCheck = false;

                if (token.IsCancellationRequested) break;

                AddLogMessage($"{(processedFileIdsInThisSession.IsEmpty ? "Первичная" : "Повторная")} проверка файлов за период с {dtB:dd.MM.yyyy} по {dtE:dd.MM.yyyy HH:mm:ss}...");
                StatusMessage = "Получение списка файлов...";

                DataTable dtTab = null;
                int currentTotalFiles = 0;

                try
                {
                    using (SqlConnection conBase = new SqlConnection(targetDbConnectionString))
                    {
                        await conBase.OpenAsync(token);
                        dtTab = await FetchFileListAsync(targetDbConnectionString, dtB, dtE, themeId, token);
                        currentTotalFiles = dtTab?.Rows.Count ?? 0;

                        if (firstCheck && currentTotalFiles > 0) 
                        {
                             TotalFiles = currentTotalFiles; 
                             AddLogMessage($"Обнаружено {TotalFiles} файлов для обработки за период.");
                             if (basePath == null && srcID == 0 && dtTab.Rows.Count > 0)
                             {
                                 try { /* ... код определения basePath ... */ }
                                 catch (Exception pathEx) { AddLogMessage($"Ошибка при определении базового пути: {pathEx.Message}"); }
                             }
                             await InitializeDateStatisticsAsync(dtTab);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AddLogMessage($"Критическая ошибка при получении списка файлов: {ex.Message}. Проверка будет повторена через {checkInterval.TotalMinutes} минут.", "Error");
                    StatusMessage = "Ошибка получения списка файлов.";
                    continue;
                }

                if (dtTab == null || dtTab.Rows.Count == 0)
                {
                    AddLogMessage("Не найдено файлов для обработки в указанном диапазоне или произошла ошибка при получении списка.");
                    continue;
                }

                var filesToProcess = dtTab.AsEnumerable()
                                        .Where(row => !processedFileIdsInThisSession.ContainsKey(Convert.ToInt32(row["documentMetaID"])))
                                        .ToList();

                if (!filesToProcess.Any())
                {
                    AddLogMessage("Новых файлов для обработки не найдено в этой проверке.");
                    continue;
                }

                AddLogMessage($"Найдено {filesToProcess.Count} новых файлов для обработки в этой проверке.");
                StatusMessage = $"Обработка {filesToProcess.Count} новых файлов...";

                var tasks = new List<Task>();
                var progressReporter = new Progress<double>(progress => DownloadProgress = progress);

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

                            await ProcessFileAsync(row, targetDbConnectionString, iacConnectionString, databaseName, srcID, flProv, themeId, token, progressReporter);

                            processedFileIdsInThisSession.TryAdd(documentMetaId, true);

                            Interlocked.Increment(ref _processedFilesCounter);
                            _processedDatesSinceLastUpdate.Enqueue(publishDate);
                        }
                        catch (OperationCanceledException)
                        {
                             AddLogMessage($"Обработка файла (ID: {documentMetaId}) отменена.", "Warning");
                        }
                        catch (Exception ex)
                        {
                            string originalFileName = row.Table.Columns.Contains("fileName") ? row["fileName"].ToString() : $"ID: {documentMetaId}";
                            AddLogMessage($"Ошибка при обработке файла '{originalFileName}': {ex.Message}", "Error");
                            if (!IgnoreDownloadErrors)
                            {
                                AddLogMessage($"Обработка файла '{originalFileName}' пропущена из-за ошибки.", "Warning");
                            }
                            else
                            {
                                AddLogMessage($"Ошибка обработки файла '{originalFileName}' проигнорирована согласно настройкам.", "Warning");
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
                }
                catch (OperationCanceledException)
                {
                     AddLogMessage("Операция отменена пользователем во время обработки файлов.", "Warning");
                    throw;
                }

                AddLogMessage("Обработка текущей пачки новых файлов завершена.");

            }

            if (token.IsCancellationRequested)
            {
                finalStatus = "Загрузка отменена пользователем.";
                AddLogMessage(finalStatus, "Warning");
            }
            else if (DateTime.Now > dtE)
            {
                finalStatus = $"Мониторинг завершен. Достигнута конечная дата: {dtE:dd.MM.yyyy HH:mm:ss}.";
                AddLogMessage(finalStatus);
            } else {
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
            AddLogMessage($"Критическая ошибка: {ex.ToString()}", "Error");
        }
        finally
        {
            _uiUpdateTimer.Stop();
            UpdateUiFromTimerTick(); 
            
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
    }

    private void UiUpdateTimer_Tick(object sender, EventArgs e)
    {
        Application.Current?.Dispatcher?.BeginInvoke(
            new Action(() => UpdateUiFromTimerTick()),
            DispatcherPriority.Background
        );
    }

    private void UpdateUiFromTimerTick()
    {
        long currentTotalProcessed = Interlocked.Read(ref _processedFilesCounter);
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
                if (_fileCountsDict.TryGetValue(kvp.Key, out var dailyStat))
                {
                    dailyStat.ProcessedCount += kvp.Value;
                }
                else
                {
                    AddLogMessage($"UpdateUiFromTimerTick: Не найдена статистика в словаре для даты {kvp.Key:dd.MM.yyyy}.", "Warning");
                    var statFromList = FileCountsPerDate.FirstOrDefault(d => d.Date == kvp.Key);
                    if (statFromList != null)
                        statFromList.ProcessedCount += kvp.Value;
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
            int removeCount = LogMessages.Count + logsToAdd.Count - 1000;
            if (removeCount > 0)
            {
                for (int i = 0; i < removeCount; i++)
                {
                    LogMessages.RemoveAt(0);
                }
            }

            foreach (var log in logsToAdd)
            {
                LogMessages.Add(log);
            }

            UpdateFilteredLogMessages();
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
        try 
        {
            // Этот метод вызывается из UpdateUiFromTimerTick в UI потоке
            if (LogMessages == null)
            {
                AddLogMessage("UpdateFilteredLogMessages: LogMessages is null!", "Error");
                return;
            }

            IEnumerable<LogMessage> messagesToShow;
            if (SelectedLogFilterType == null || SelectedLogFilterType.Type == "All")
            {
                messagesToShow = LogMessages.ToList(); // Создаем копию
            }
            else
            {
                messagesToShow = LogMessages.Where(m => m.Type == SelectedLogFilterType.Type).ToList();
            }

            // Оптимизация: Очищаем существующую коллекцию и добавляем элементы
            _filteredLogMessages.Clear();
            foreach (var message in messagesToShow)
            {
                _filteredLogMessages.Add(message);
            }
        }
        catch (Exception ex)
        {
            // Логируем ошибку в файл, так как логи UI могут не работать
            FileLogger.Log($"Ошибка в UpdateFilteredLogMessages: {ex}");
        }
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

    private void CancelDownload()
    {
        if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
        {
            AddLogMessage("Запрос на отмену операции...");
            _cancellationTokenSource.Cancel();
        }
    }

    private async Task<DataTable> FetchFileListAsync(string connectionString, DateTime dtB, DateTime dtE, int themeId, CancellationToken token)
    {
        DataTable dtTab = new DataTable();
        // Используем переданную строку подключения
        using (SqlConnection conBase = new SqlConnection(connectionString))
        {
            await conBase.OpenAsync(token);
            using (SqlCommand cmd = new SqlCommand("documentMetaDownloadList", conBase))
            {
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.Parameters.AddWithValue("@themeID", themeId);
                cmd.Parameters.AddWithValue("@dtB", dtB);
                cmd.Parameters.AddWithValue("@dtE", dtE);
                cmd.Parameters.AddWithValue("@srcID", 1); // Используем 1, как в примере хранимой процедуры

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
        // Извлекаем ТОЛЬКО ТЕ данные, которые возвращает процедура documentMetaDownloadList
        string url = row["url"].ToString();
        DateTime publishDate = DateTime.Parse(row["publishDate"].ToString());
        string computerName = row["computerName"].ToString();        // Из переменной @computerName
        string directoryName = row["directoryName"].ToString();      // Из переменной @directoryName
        int documentMetaID = Convert.ToInt32(row["documentMetaID"].ToString()); // Из atch.attachmentID
        string originalFileName = row["fileName"].ToString();        // Из atch.fileName
        string expName = row["expName"].ToString();                // Рассчитанный
        string docDescription = row["docDescription"].ToString();    // Из atch.docDescription
        object urlIdFromDb = row["urlID"];                      // Из atch.urlID

        // Удалено извлечение несуществующих столбцов:
        // documentMetaPathID, pthDocument, flDocumentOriginal, ftp, fileNameFtp

        long currentCount = Interlocked.Increment(ref _processedFilesCounter);
        bool shouldUpdateUI = currentCount % 5 == 0;

        // Логика суффикса остается
        string suffixName = "";
        if (databaseName == "notificationEF") suffixName = "_nef";
        else if (databaseName == "notificationZK") suffixName = "_nzk";
        else if (databaseName == "notificationOK") suffixName = "_nok";

        // --- Унифицированная логика формирования пути и имени файла ---\n        // Используем данные, которые точно есть
        string pathDocument = $@"""\\{computerName}\{directoryName}\{themeId}\{publishDate.Year}\{publishDate.Month}\{publishDate.Day}\";
        string fileDocument = Path.Combine(pathDocument, $"{documentMetaID}{suffixName}.{expName}");
        // Логика с srcID и fileNameFtp удалена, т.к. fileNameFtp не возвращается процедурой

        // Проверяем, что путь и имя сформированы корректно
        if (string.IsNullOrWhiteSpace(pathDocument))
        {
            throw new InvalidOperationException($"Не удалось сформировать путь к директории для documentMetaID: {documentMetaID}");
        }
        if (string.IsNullOrWhiteSpace(fileDocument))
        {
            throw new InvalidOperationException($"Не удалось сформировать путь к файлу для documentMetaID: {documentMetaID}");
        }
        // Удалена проверка if (srcID != 0 && string.IsNullOrWhiteSpace(pathDocument)), т.к. pathDocument теперь всегда формируется

        try // Основной try для ProcessFileAsync
        {
            if (flProv == false)
            {
                if (!string.IsNullOrEmpty(pathDocument))
                {
                    Directory.CreateDirectory(pathDocument);
                }
                else if (srcID == 0)
                {
                    string dirOfFile = Path.GetDirectoryName(fileDocument);
                    if (!string.IsNullOrEmpty(dirOfFile))
                    {
                        Directory.CreateDirectory(dirOfFile);
                    }
                    else
                    {
                        AddLogMessage($"Предупреждение: не удалось определить директорию для создания для файла {fileDocument}");
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Путь к директории не определен для srcID={srcID}, file={fileDocument}");
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
                const int maxRetries = 3;
                int retryDelaySeconds = 1;

                try
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
                        await Task.Delay(currentAdaptiveDelay, token);
                    }

                    for (int attempt = 1; attempt <= maxRetries; attempt++)
                    {
                        token.ThrowIfCancellationRequested();

                        if (attempt > 1 || shouldUpdateUI)
                        {
                            AddLogMessage($"Попытка скачивания #{attempt} для: {originalFileName}");
                        }

                        downloadResult = await WebGetAsync(url, fileDocument, token, progress);

                        if (downloadResult.Success)
                        {
                            fileSize = downloadResult.ActualSize;
                            downloadSucceeded = true;

                            if (shouldUpdateUI)
                            {
                                AddLogMessage($"Файл '{originalFileName}' скачан успешно (попытка {attempt}), размер: {fileSize} байт.");
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
                        else if (downloadResult.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxRetries)
                        {
                            int delaySeconds = retryDelaySeconds;
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
                        throw new FileNotFoundException($"Файл '{fileDocument}' не существует или имеет неверный размер после скачивания.");
                    }

                    // --- Обработка архивов --- 
                    // Список проверяемых расширений архивов (регистронезависимо)
                    var archiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz" 
                        // Добавьте другие, если нужно
                    };
                    bool isArchive = archiveExtensions.Contains("." + expName);

                    if (isArchive)
                    {
                        try
                        {
                            AddLogMessage($"Распаковка архива: {fileDocument} в {pathDocument}", "Info");
                            // Вызываем новый универсальный метод
                            _archiveService.ExtractArchive(fileDocument, pathDocument, true); // true - разрешаем перезапись
                            // Удаление архива после успешной распаковки
                            try { File.Delete(fileDocument); AddLogMessage($"Исходный архив удален: {fileDocument}", "Info"); } catch (Exception delEx) { _logger.Warn(delEx, $"Не удалось удалить исходный архив {fileDocument}"); }
                        }
                        catch (Exception archiveEx)
                        {
                            // Если распаковка не удалась, логируем ошибку, но НЕ прерываем процесс,
                            // чтобы флаг для самого архива все равно был обновлен в базе.
                            _logger.Error(archiveEx, $"Ошибка при распаковке архива {fileDocument}");
                            AddLogMessage($"Ошибка распаковки архива '{originalFileName}': {archiveEx.Message}", "Error");
                        }
                    }

                    // Обновляем флаг загрузки в базе данных для ИСХОДНОГО файла (архива или нет)
                    try
                    {
                        AddLogMessage($"Обновление флага загрузки для файла ID: {documentMetaID} в базе {databaseName}...", "Info");
                        await _databaseService.UpdateDownloadFlagAsync(targetDbConnectionString, documentMetaID, token);
                        AddLogMessage($"Флаг для файла ID: {documentMetaID} успешно обновлен.", "Success");
                    }
                    catch (Exception updateEx)
                    {
                        _logger.Error(updateEx, $"Ошибка при обновлении флага загрузки для файла ID: {documentMetaID} в базе {databaseName}");
                        AddLogMessage($"Не удалось обновить флаг загрузки для файла '{originalFileName}'. Ошибка: {updateEx.Message}", "Error");
                        // Решаем, что делать дальше. Возможно, стоит перебросить исключение,
                        // чтобы обработка файла считалась неуспешной в целом?
                        // Пока просто логируем и продолжаем.
                    }


                }
                catch (Exception ex) // Внутренний catch для ошибок скачивания/проверки
                {
                    // Логируем ошибку, которая произошла внутри цикла попыток скачивания
                    _logger.Error(ex, $"Внутренняя ошибка при попытке скачивания файла '{originalFileName}' ({url})");
                    // Перебрасывать не будем, т.к. основная логика обработки ошибок скачивания выше
                    // и мы не хотим попасть во внешний catch для той же ошибки.
                }

            }
        }
        catch (Exception ex) // Основной catch метода ProcessFileAsync
        {
            // Логируем ошибку обработки файла
            _logger.Error(ex, $"Критическая ошибка при обработке файла '{originalFileName}' (ID: {documentMetaID})");
            // Добавляем сообщение в UI лог
            AddLogMessage($"Ошибка обработки файла '{originalFileName}': {ex.Message}", "Error");
            // Перебрасываем исключение, чтобы оно было обработано в StartDownloadAsync
            throw;
        }
    }


    

    // --- НОВЫЕ Методы для статистики по датам ---
    private async Task InitializeDateStatisticsAsync(DataTable fileTable)
    {
        if (fileTable == null) return;
        AddLogMessage("InitializeDateStatisticsAsync: Расчет начальной статистики...");
        try
        {
            var countsByDateList = await Task.Run(() =>
            {
                return fileTable.AsEnumerable()
                    .Where(row => row["publishDate"] != DBNull.Value)
                    .GroupBy(row => DateTime.Parse(row["publishDate"].ToString()).Date)
                    .Select(g => new { Date = g.Key, Count = g.Count() })
                    .OrderBy(x => x.Date)
                    .ToList();
            });

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
        // Реализация этого метода уже была добавлена ранее
        // Оставляем её как есть или добавляем, если отсутствует
        if (fileTable == null) return;
        AddLogMessage("UpdateDateStatisticsAsync: Обновление статистики...");
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
                    }
                 }
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
                        AddLogMessage($"LoadAvailableDatabasesAsync: База {displayName} доступна.", "Success");
                    }
                }
                catch (Exception ex)
                {
                    AddLogMessage($"LoadAvailableDatabasesAsync: База {db} недоступна: {ex.Message}", "Warning");
                }
            }
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
        }
        catch (Exception ex)
        {
            AddLogMessage($"LoadAvailableThemesAsync: Ошибка при загрузке тем: {ex.Message}", "Error");
            await Application.Current.Dispatcher.InvokeAsync(() =>
                SetProperty(ref _availableThemes, new ObservableCollection<ThemeInfo>(), nameof(AvailableThemes))
            );
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

    private void LoadConfigurationAndSettings()
    {
        try
        {
            // Используем внедренный ConfigurationService
            _baseConnectionString = _configurationService.GetBaseConnectionString();
            _iacConnectionString = _configurationService.GetIacConnectionString();
            _serverOfficeConnectionString = _configurationService.GetServerOfficeConnectionString();

            FileLogger.Log($"Retrieved BaseConnectionString: '{_baseConnectionString}'");
            FileLogger.Log($"Retrieved IacConnectionString: '{_iacConnectionString}'");
            FileLogger.Log($"Retrieved ServerOfficeConnectionString: '{_serverOfficeConnectionString}'");

            // Проверяем наличие ServerOfficeConnection, так как он теперь базовый
            if (string.IsNullOrEmpty(_serverOfficeConnectionString))
            {
                AddLogMessage("ОШИБКА: Строка подключения ServerOfficeConnection не найдена.", "Error");
                _baseConnectionString = null; // Не можем работать без базового сервера
            }
            else
            {
                // Формируем базовую строку из ServerOfficeConnection, убрав Initial Catalog
                var serverOfficeBuilder = new SqlConnectionStringBuilder(_serverOfficeConnectionString);
                string baseServer = serverOfficeBuilder.DataSource;
                serverOfficeBuilder.Remove("Initial Catalog");
                _baseConnectionString = serverOfficeBuilder.ConnectionString;
                AddLogMessage($"Базовая строка подключения установлена на сервер: {baseServer} (из ServerOfficeConnection)", "Info");
            }

            // Загрузка настроек приложения
            CurrentSettings = _configurationService.GetApplicationSettings() ?? new ApplicationSettings();
            AddLogMessage($"Настройки загружены. Потоков: {CurrentSettings.MaxParallelDownloads}, Пауза: {CurrentSettings.SleepIntervalMilliseconds} мс", "Info");

            // Загрузка настроек FTP
            CurrentFtpSettings = _configurationService.GetFtpSettings() ?? new FtpSettings();
        }
        catch (Exception ex)
        {
            AddLogMessage($"КРИТИЧЕСКАЯ ОШИБКА при загрузке конфигурации: {ex.Message}", "Error");
            CurrentSettings = new ApplicationSettings(); // Default settings
            CurrentFtpSettings = new FtpSettings(); // Default settings
            _baseConnectionString = null;
            _iacConnectionString = null;
            _serverOfficeConnectionString = null;
        }
    }

    private async Task InitializeAsync()
    {
        AddLogMessage("Начало асинхронной инициализации...");
        try
        {
            Task dbLoadTask = LoadAvailableDatabasesAsync();
            Task themeLoadTask = LoadAvailableThemesAsync();

            await Task.WhenAll(dbLoadTask, themeLoadTask);

            _isInitialized = true;
            AddLogMessage($"_isInitialized установлен в {_isInitialized}", "Info");

            // Устанавливаем значения по умолчанию, если они не выбраны
            if (SelectedDatabase == null && AvailableDatabases.Any())
            {
                SelectedDatabase = AvailableDatabases.First();
                AddLogMessage($"База данных по умолчанию установлена: {SelectedDatabase.DisplayName}", "Info");
            }
            if (SelectedTheme == null && AvailableThemes.Any())
            {
                SelectedTheme = AvailableThemes.First();
                 AddLogMessage($"Тема по умолчанию установлена: {SelectedTheme.Name}", "Info");
            }

            StatusMessage = "Готов";
            AddLogMessage("Асинхронная инициализация успешно завершена.", "Success");

            // Уведомляем UI поток об изменении состояния команды StartDownload
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (StartDownloadCommand is AsyncRelayCommand rc)
                {
                    rc.NotifyCanExecuteChanged();
                    AddLogMessage("NotifyCanExecuteChanged вызван для StartDownloadCommand из InitializeAsync", "Info");
                }
                else
                {
                    AddLogMessage("ОШИБКА: StartDownloadCommand не является AsyncRelayCommand!", "Error");
                }
            });
        }
        catch (Exception ex)
        {
            _isInitialized = false;
            StatusMessage = "Ошибка инициализации";
            AddLogMessage($"Критическая ошибка во время асинхронной инициализации: {ex.Message}", "Error");
            FileLogger.Log($"InitializeAsync Exception: {ex}");
        }
    }

    // --- Реализация IDataErrorInfo ---
    public string Error => null; // Общую ошибку не используем

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
                    // Добавьте другие проверки при необходимости
            }
            // Уведомляем команду о возможном изменении состояния CanExecute
            if (StartDownloadCommand is AsyncRelayCommand rc) rc.NotifyCanExecuteChanged(); // ИЗМЕНЕНО: AsyncRelayCommand
            return error;
        }
    }
    // --- Конец реализации IDataErrorInfo ---

    // --- Методы для команд ---
    private bool CanStartDownload()
    {
        bool notDownloading = !IsDownloading;
        bool isInit = _isInitialized;
        bool hasDb = SelectedDatabase != null;
        bool hasTheme = SelectedTheme != null;
        bool canStart = notDownloading && isInit && hasDb && hasTheme;

        AddLogMessage($"CanStartDownload проверка:" +
            $"\n - !IsDownloading: {notDownloading}" +
            $"\n - _isInitialized: {isInit}" +
            $"\n - SelectedDatabase: {(hasDb ? SelectedDatabase.DisplayName : "NULL")}" +
            $"\n - SelectedTheme: {(hasTheme ? SelectedTheme.Name : "NULL")}" +
            $"\n - ИТОГ: {canStart}", "Info");

        return canStart;
    }

    private bool CanCancelDownload()
    {
        // Можно отменить, только если идет загрузка.
        return IsDownloading;
    }

    private void ClearLog()
    {
        LogMessages.Clear();
        UpdateFilteredLogMessages(); // Обновляем и отфильтрованные сообщения
        AddLogMessage("Лог очищен.");
        // Уведомляем команды, зависящие от лога, об изменении
        if (ClearLogCommand is RelayCommand clc) clc.NotifyCanExecuteChanged();
        if (CopyLogToClipboardCommand is RelayCommand cplc) cplc.NotifyCanExecuteChanged();
    }

    private bool CanClearLog()
    {
        // Можно очистить, если есть сообщения
        return LogMessages.Count > 0;
    }

    private void CopyLogToClipboard()
    {
        try
        {
            var logText = string.Join(Environment.NewLine,
                FilteredLogMessages.Select(lm => $"[{lm.Timestamp:G}] [{lm.Type}] {lm.Message}"));
            if (!string.IsNullOrEmpty(logText))
            {
                SetText(logText);
                AddLogMessage("Отфильтрованный лог скопирован в буфер обмена.");
            }
            else
            {
                AddLogMessage("Нет сообщений для копирования (с учетом фильтра).", "Warning");
            }
        }
        catch (Exception ex)
        {
            AddLogMessage($"Ошибка копирования лога в буфер обмена: {ex.Message}", "Error");
        }
    }

    private bool CanCopyLog()
    {
        // Можно скопировать, если есть отфильтрованные сообщения
        return FilteredLogMessages.Count > 0;
    }

    private void OpenLogDirectory()
    {
        try
        {
            // Предполагаем, что логи пишутся в подпапку logs рядом с exe
            string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            if (Directory.Exists(logDirectory))
            {
                // Открываем папку в проводнике
                Process.Start(new ProcessStartInfo
                {
                    FileName = logDirectory,
                    UseShellExecute = true // Важно для открытия папок
                });
                AddLogMessage($"Папка логов открыта: {logDirectory}");
            }
            else
            {
                AddLogMessage($"Папка логов не найдена: {logDirectory}", "Warning");
            }
        }
        catch (Exception ex)
        {
            AddLogMessage($"Ошибка при открытии папки логов: {ex.Message}", "Error");
        }
    }
    // --- Конец методов для команд ---

    // Добавляем приватный метод для применения темы
    private void ApplyUiTheme()
    {
        if (!string.IsNullOrEmpty(SelectedBaseUiTheme) && !string.IsNullOrEmpty(SelectedAccentUiColor))
        {
            try
            {
                ThemeManager.Current.ChangeTheme(Application.Current, $"{SelectedBaseUiTheme}.{SelectedAccentUiColor}");
                _logger.Info($"Тема интерфейса изменена на: {SelectedBaseUiTheme}.{SelectedAccentUiColor}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Ошибка при применении темы UI: {SelectedBaseUiTheme}.{SelectedAccentUiColor}");
                // Можно добавить уведомление пользователю, если нужно
            }
        }
    }

    // Метод для заполнения списков тем
    private void PopulateThemeSelectors()
    {
        // Получаем доступные базовые темы
        var baseThemes = ThemeManager.Current.BaseColors;
        AvailableBaseUiThemes.Clear();
        foreach (var theme in baseThemes)
        {
            AvailableBaseUiThemes.Add(theme);
        }

        // Получаем доступные акцентные цвета
        var accentColors = ThemeManager.Current.ColorSchemes;
        AvailableAccentUiColors.Clear();
        foreach (var accent in accentColors)
        {
            AvailableAccentUiColors.Add(accent);
        }
    }
}
       
