namespace FileDownloader.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using FileDownloader.Infrastructure;
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

    static DownloaderViewModel()
    {
        // Настройка обработчика HTTP
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

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

        LoadAvailableDatabases();
        FileLogger.Log("Конструктор DownloaderViewModel: LoadAvailableDatabases завершен");
        
        // Загружаем темы сразу после конфигурации, т.к. они из IAC
        LoadAvailableThemes(); 
        FileLogger.Log("Конструктор DownloaderViewModel: LoadAvailableThemes завершен");

        LoadUiThemesAndAccents();
        FileLogger.Log("Конструктор DownloaderViewModel: LoadUiThemesAndAccents завершен");

        // Инициализация команд
        StartDownloadCommand = new RelayCommand(
            async () => await StartDownloadAsync(),
            () => !IsDownloading && string.IsNullOrEmpty(Error) && !string.IsNullOrEmpty(_baseConnectionString) && SelectedDatabase != null && SelectedTheme != null
        );
        CancelDownloadCommand = new RelayCommand(
            () => CancelDownload(),
            () => IsDownloading
        );
        OpenSettingsCommand = new RelayCommand(OpenSettingsWindow, () => !IsDownloading);
        ClearLogCommand = new RelayCommand(
            () => LogMessages.Clear(),
            () => LogMessages.Count > 0
        );
        CopyLogToClipboardCommand = new RelayCommand(
            () => SetText(string.Join(Environment.NewLine, LogMessages)),
            () => LogMessages.Count > 0
        );
        FileLogger.Log("Конструктор DownloaderViewModel: Команды инициализированы");

        StatusMessage = "Готов"; // Начальный статус
        FileLogger.Log("Конструктор DownloaderViewModel: Завершен");
    }

    // --- Основная логика ---

    private async Task StartDownloadAsync()
    {
        if (SelectedTheme == null)
        {
            AddLogMessage("ОШИБКА: Не выбрана тема");
            return;
        }

        if (string.IsNullOrEmpty(SelectedDatabase?.Name))
        {
            AddLogMessage("ОШИБКА: Не выбрана база данных");
            return;
        }

        if (BeginDate == default || EndDate == default)
        {
            AddLogMessage("ОШИБКА: Не выбран диапазон дат");
            return;
        }

        if (EndDate < BeginDate)
        {
            AddLogMessage("ОШИБКА: Дата окончания меньше даты начала");
            return;
        }

        if (!string.IsNullOrEmpty(Error) || SelectedDatabase == null || SelectedTheme == null)
        { AddLogMessage("Ошибка валидации параметров. Загрузка не начата."); return; }
        if (IsDownloading) return;

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        IsDownloading = true;
        (StartDownloadCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (CancelDownloadCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (OpenSettingsCommand as RelayCommand)?.NotifyCanExecuteChanged();

        LogMessages.Clear();
        AddLogMessage("Начало загрузки...");
        TotalFiles = 0;
        ProcessedFiles = 0;
        CurrentFileName = "";
        StatusMessage = "Загрузка..."; // Статус в начале загрузки

        string databaseName = SelectedDatabase.Name;
        int themeId = SelectedTheme.Id;
        int srcID = SelectedSourceId;
        bool flProv = CheckProvError;

        bool success = false;
        int retryCount = 0;
        const int maxRetries = 3;
        var semaphore = new SemaphoreSlim(CurrentSettings.MaxParallelDownloads);

        var conStrBuilder = new SqlConnectionStringBuilder(_baseConnectionString) { InitialCatalog = databaseName };
        string targetDbConnectionString = conStrBuilder.ConnectionString;
        string iacConnectionString = _iacConnectionString;

        long processedFilesCounter = 0;
        IProgress<double> progressReporter = null;
        string basePath = null; // Переменная для хранения базового пути

        try
        {
            while (!success && retryCount < maxRetries && !token.IsCancellationRequested)
            {
                DataTable dtTab = null;
                try
                {
                    using (var conBase = new SqlConnection(targetDbConnectionString))
                    {
                        AddLogMessage("Получение списка файлов...");
                        await conBase.OpenAsync(token);
                        dtTab = await FetchFileListAsync(conBase, BeginDate, EndDate, themeId, srcID, SelectedFilterId, flProv, token);
                        TotalFiles = dtTab?.Rows.Count ?? 0;
                        AddLogMessage($"Получено {TotalFiles} файлов для обработки.");

                        // Получаем базовый путь, если есть файлы и srcID=0 (для других srcID путь формируется иначе)
                        if (TotalFiles > 0 && srcID == 0)
                        {
                            try
                            {
                                string computerName = dtTab.Rows[0]["computerName"]?.ToString();
                                string directoryName = dtTab.Rows[0]["directoryName"]?.ToString();
                                if (!string.IsNullOrEmpty(computerName) && !string.IsNullOrEmpty(directoryName))
                                {
                                    basePath = $"\\\\{computerName}\\{directoryName}"; // Используем @ для буквальной строки или двойные слеши
                                    AddLogMessage($"Базовый путь для сохранения: {basePath}");
                                }
                                else { AddLogMessage("Не удалось определить базовый путь (computerName/directoryName пусты)."); }
                            }
                            catch (Exception pathEx) { AddLogMessage($"Ошибка при определении базового пути: {pathEx.Message}"); }
                        }
                    }

                    if (dtTab == null || TotalFiles == 0 || token.IsCancellationRequested)
                    { success = true; break; }

                    AddLogMessage($"Запуск параллельной обработки ({CurrentSettings.MaxParallelDownloads} потоков)...");
                    List<Task> processingTasks = dtTab.AsEnumerable().Select(async row =>
                    {
                        await semaphore.WaitAsync(token);
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            string currentFileNameLocal = row["fileName"].ToString();
                            AddLogMessage($"Начало обработки: {currentFileNameLocal}");
                            await ProcessFileAsync(row, targetDbConnectionString, iacConnectionString, databaseName, srcID, flProv, themeId, token, progressReporter);
                            long currentCount = Interlocked.Increment(ref processedFilesCounter);
                            Application.Current.Dispatcher.Invoke(() => ProcessedFiles = (int)currentCount); // Обновляем UI потокобезопасно
                            AddLogMessage($"Завершено: {currentFileNameLocal} ({currentCount}/{TotalFiles})");
                        }
                        catch (OperationCanceledException) { AddLogMessage($"Отменена обработка для файла: {row["fileName"]}"); }
                        catch (Exception fileEx)
                        {
                            AddLogMessage($"ОШИБКА при обработке файла {row["fileName"]}: {fileEx.Message}");
                            if (!IgnoreDownloadErrors) throw;
                            else { Interlocked.Increment(ref processedFilesCounter); Application.Current.Dispatcher.Invoke(() => ProcessedFiles = (int)processedFilesCounter); }
                        }
                        finally { semaphore.Release(); }
                    }).ToList();

                    await Task.WhenAll(processingTasks);
                    if (!token.IsCancellationRequested) { AddLogMessage("Параллельная обработка завершена."); success = true; }
                }
                catch (OperationCanceledException) { throw; }
                catch (AggregateException aggEx)
                {
                    success = false;
                    AddLogMessage($"ОШИБКА во время параллельной обработки:");
                    foreach (var innerEx in aggEx.Flatten().InnerExceptions)
                    { if (innerEx is OperationCanceledException) continue; AddLogMessage($"- {innerEx.Message}"); }
                    if (!IgnoreDownloadErrors) { retryCount = maxRetries; break; } // Прерываем если есть ошибки и не игнорируем
                    else { AddLogMessage("Ошибки файлов проигнорированы."); success = true; break; } // Считаем завершенным с ошибками
                }
                catch (Exception ex) // Ошибки подключения или получения списка
                {
                    retryCount++;
                    AddLogMessage($"ОШИБКА подключения/получения списка ({retryCount}/{maxRetries}): {ex.Message}");
                    if (retryCount >= maxRetries) { AddLogMessage("Превышено количество попыток. Загрузка остановлена."); StatusMessage = "Ошибка подключения/списка."; }
                    else { AddLogMessage($"Повторная попытка через 5 секунд..."); await Task.Delay(5000, token); }
                }
            } // конец while
        }
        catch (OperationCanceledException)
        { success = false; StatusMessage = "Операция отменена пользователем."; AddLogMessage("Операция отменена."); }
        catch (Exception ex) // Другие глобальные ошибки
        { success = false; StatusMessage = "Критическая ошибка."; AddLogMessage($"КРИТИЧЕСКАЯ ОШИБКА: {ex.Message}"); }
        finally
        {
            IsDownloading = false;
            string finalStatus = "";
            if (token.IsCancellationRequested) { finalStatus = "Операция отменена пользователем."; }
            else if (StatusMessage == "Критическая ошибка." || StatusMessage == "Ошибка подключения/списка.") { finalStatus = StatusMessage; } // Оставляем сообщение об ошибке
            else { finalStatus = success ? "Загрузка завершена." : "Загрузка завершена с ошибками."; }

            // Добавляем информацию о пути, если он был определен и что-то обработано
            if (processedFilesCounter > 0 && !string.IsNullOrEmpty(basePath))
            {
                finalStatus += $" Файлы сохранены в: {basePath}\\...";
                AddLogMessage($"Успешно обработанные файлы сохранены в подпапки директории: {basePath}"); // Добавляем в лог
            }
            else if (processedFilesCounter > 0 && srcID != 0)
            {
                 // Для других srcID (FTP, Локальный) путь может быть другим, просто сообщим о завершении
                 AddLogMessage($"Обработка файлов для источника ID={srcID} завершена.");
            }

            StatusMessage = finalStatus; // Устанавливаем итоговый статус
            CurrentFileName = "";
            _cancellationTokenSource?.Dispose(); _cancellationTokenSource = null;
            (StartDownloadCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (CancelDownloadCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (OpenSettingsCommand as RelayCommand)?.NotifyCanExecuteChanged();

            // В блоке finally обновляем статус загрузки
            UpdateFlag = false;
        }
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
                AddLogMessage($"Скачивание: {url} -> {fileDocument}");
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

                    Application.Current.Dispatcher.Invoke(() => CurrentFileName = originalFileName); // Обновляем UI перед скачиванием

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
                        AddLogMessage($"Попытка скачивания #{attempt} для: {originalFileName}");
                        downloadResult = await WebGetAsync(url, fileDocument, token, progress);

                        if (downloadResult.Success)
                        {
                            fileSize = downloadResult.ActualSize;
                            downloadSucceeded = true;
                            AddLogMessage($"Файл '{originalFileName}' скачан успешно (попытка {attempt}), размер: {fileSize} байт.");
                            break; // Выходим из цикла повторов при успехе
                        }
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
                        else // Успех или другая ошибка
                        {
                            if (downloadResult.Success)
                            {
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
                            else
                            {
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
                        }
                    }

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

        Application.Current?.Dispatcher?.Invoke(() =>
        {
            if (LogMessages.Count > 1000)
            {
                LogMessages.RemoveAt(0);
            }
            LogMessages.Add(logMessage);
            UpdateFilteredLogMessages();
        });
    }

    private void UpdateFilteredLogMessages()
    {
        if (SelectedLogFilterType == null || SelectedLogFilterType.Type == "All")
        {
            FilteredLogMessages = new ObservableCollection<LogMessage>(LogMessages);
        }
        else
        {
            FilteredLogMessages = new ObservableCollection<LogMessage>(
                LogMessages.Where(m => m.Type == SelectedLogFilterType.Type)
            );
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
            var databases = new[] { "notificationEF", "notificationZK", "notificationOK", "fcsNotification", "contract", "purchaseNotice", "requestQuotation" };
            var availableDbs = new List<DatabaseInfo>();

            AddLogMessage($"LoadAvailableDatabases: Начало цикла проверки {databases.Length} баз данных.", "Info"); // Добавлено
            foreach (var db in databases)
            {
                try
                {
                    var connectionString = _baseConnectionString.Replace("Initial Catalog=notificationEF", $"Initial Catalog={db}"); // Оставим Replace пока что
                    AddLogMessage($"LoadAvailableDatabases: Попытка подключения к {db} ({connectionString})", "Info"); // Добавлено
                    using (var connection = new SqlConnection(connectionString))
                    {
                        // Установим короткий таймаут для быстрой проверки
                        connection.Open(); // Используем стандартный таймаут
                        availableDbs.Add(new DatabaseInfo { Name = db });
                        AddLogMessage($"LoadAvailableDatabases: База данных {db} доступна.", "Success"); // Изменено
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
            AddLogMessage($"Настройки загружены. Потоков: {CurrentSettings.MaxParallelDownloads}, Пауза: {CurrentSettings.SleepIntervalMilliseconds} мс");

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

    // Добавляем определение для AvailableThemes
    private ObservableCollection<ThemeInfo> _availableThemes = new ObservableCollection<ThemeInfo>();
    public ObservableCollection<ThemeInfo> AvailableThemes
    {
        get => _availableThemes;
        private set => SetProperty(ref _availableThemes, value);
    }
} 