namespace DownloaderApp.ViewModels;

using System;
using System.Collections.Generic; // Required for EqualityComparer
using System.Collections.ObjectModel; // Для списков, обновляющих UI
using System.ComponentModel;
using System.Data;
// using System.Data.SqlClient; // <-- Закомментировано
using Microsoft.Data.SqlClient; // <-- Добавлено
using System.IO;
using System.Net.Http; // Для HttpClient в WebGetAsync
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks; // Для асинхронности
using System.Windows; // Для Application.Current.Dispatcher
using System.Windows.Input; // Для команд
using DownloaderApp.Infrastructure; // Добавляем using
using Microsoft.Extensions.Configuration;
using FluentFTP;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using DownloaderApp.Models; // Добавляем using для моделей
using DownloaderApp.Views; // Для SettingsWindow
using System.Diagnostics; // Для Debug.WriteLine
using System.Linq;     // Для Select
using FluentFTP.Exceptions; // Добавляем using для FtpCommandException
using ControlzEx.Theming; // <-- Добавлено для MahApps ThemeManager

// --- Добавляем класс DownloadResult ---
public class DownloadResult
{
    public bool Success { get; set; }
    public long? ExpectedSize { get; set; } // Ожидаемый размер из Content-Length (может быть null)
    public long ActualSize { get; set; }     // Фактический размер скачанного файла
    public HttpStatusCode? StatusCode { get; set; } // HTTP статус ответа
    public string ErrorMessage { get; set; } // Сообщение об ошибке
    public string TempFilePath { get; set; } // Путь к временному файлу (если он остался)
}
// --- Конец класса DownloadResult ---

public class DownloaderViewModel : ObservableObject, IDataErrorInfo
{
    // --- Текущие Активные Настройки ---
    private ApplicationSettings _currentSettings = new ApplicationSettings();
    public ApplicationSettings CurrentSettings
    {
        get => _currentSettings;
        private set => SetProperty(ref _currentSettings, value);
    }

    // --- Параметры загрузки (Свойства, к которым будет привязан UI) ---

    // Добавляем коллекцию баз данных и свойство для выбранной базы
    public ObservableCollection<DatabaseInfo> AvailableDatabases { get; } = new ObservableCollection<DatabaseInfo>();
    private DatabaseInfo _selectedDatabase;
    public DatabaseInfo SelectedDatabase
    {
        get => _selectedDatabase;
        set => SetProperty(ref _selectedDatabase, value);
    }

    public ObservableCollection<ThemeInfo> AvailableThemes { get; } = new ObservableCollection<ThemeInfo>();
    private ThemeInfo _selectedTheme;
    public ThemeInfo SelectedTheme
    {
        get => _selectedTheme;
        set => SetProperty(ref _selectedTheme, value);
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

    // Список сообщений для лога (вместо memoStatus)
    public ObservableCollection<string> LogMessages { get; } = new ObservableCollection<string>();

    // --- Команды (Для кнопок) ---
    public ICommand StartDownloadCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    // --- CancellationTokenSource ---
    private CancellationTokenSource _cancellationTokenSource;

    // --- Свойства конфигурации --- 
    private string _baseConnectionString;
    private string _iacConnectionString;

    // --- Конструктор ---
    public DownloaderViewModel()
    {
        FileLogger.Log("Конструктор DownloaderViewModel: Начало");
        
        LoadConfigurationAndSettings(); 
        FileLogger.Log("Конструктор DownloaderViewModel: LoadConfigurationAndSettings завершен");

        LoadAvailableDatabases();
        FileLogger.Log("Конструктор DownloaderViewModel: LoadAvailableDatabases завершен");
        
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
        FileLogger.Log("Конструктор DownloaderViewModel: Команды инициализированы");

        FileLogger.Log("Конструктор DownloaderViewModel: Завершен");
    }

    // --- Основная логика ---

    private async Task StartDownloadAsync()
    {
        if (!string.IsNullOrEmpty(Error) || SelectedDatabase == null || SelectedTheme == null)
        { AddLogMessage("Ошибка валидации параметров. Загрузка не начата."); return; }
        if (IsDownloading) return;

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        IsDownloading = true;
        (StartDownloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelDownloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenSettingsCommand as RelayCommand)?.RaiseCanExecuteChanged(); // Обновляем кнопку Настроек

        LogMessages.Clear();
        AddLogMessage("Начало загрузки...");
        TotalFiles = 0;
        ProcessedFiles = 0;
        CurrentFileName = "";
        StatusMessage = "Загрузка...";

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
            (StartDownloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelDownloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenSettingsCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

            await Task.Delay(CurrentSettings.SleepIntervalMilliseconds, token); // Используем паузу из настроек

            // --- Скачивание ---
            AddLogMessage($"Скачивание: {url} -> {fileDocument}");
            long fileSize = 0;
            try
            {
                Application.Current.Dispatcher.Invoke(() => CurrentFileName = originalFileName); // Обновляем UI перед скачиванием
                await WebGetAsync(url, fileDocument, token, progress);

                FileInfo fileInfo = new FileInfo(fileDocument);
                if (fileInfo.Exists) // Проверяем, что файл реально создался
                {
                     fileSize = fileInfo.Length;
                     AddLogMessage($"Файл '{originalFileName}' скачан, размер: {fileSize} байт.");
                } else {
                     throw new FileNotFoundException("Файл не был создан после скачивания.", fileDocument);
                }
            }
            catch (Exception webEx)
            {
                 AddLogMessage($"Ошибка скачивания файла '{originalFileName}' из {url}: {webEx.Message}");
                 // Попытка удалить частично скачанный файл, если он есть
                 if (File.Exists(fileDocument)) { try { File.Delete(fileDocument); } catch { /* Ignore delete error */ } }
                 throw; // Перебрасываем ошибку
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
            using (SqlConnection conBaseI = new SqlConnection(iacConnectionString))
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
                           cmdUpdate.Parameters.Add("@prcID", SqlDbType.Int).Value = CurrentSettings.ProcessId;
                           cmdUpdate.Parameters.Add("@usrID", SqlDbType.Int).Value = CurrentSettings.UserId;
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
                               cmdUpdateDelete.Parameters.Add("@prcID", SqlDbType.Int).Value = CurrentSettings.ProcessId;
                               cmdUpdateDelete.Parameters.Add("@usrID", SqlDbType.Int).Value = CurrentSettings.UserId;
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

    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) }; // Увеличим таймаут

    // --- ЗАМЕНА WebGetAsync ---
    private async Task<DownloadResult> WebGetAsync(string url, string tempFilePath, CancellationToken token, IProgress<double> progress)
    {
        var result = new DownloadResult { Success = false, ActualSize = 0, TempFilePath = tempFilePath };
        FileLogger.Log($"WebGetAsync: Начало загрузки {url} -> {tempFilePath}");

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

    private void AddLogMessage(string message)
    {
        FileLogger.Log(message);
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            if (LogMessages.Count > 1000) { LogMessages.RemoveAt(0); }
            LogMessages.Add(message);
        });
    }

    // Метод для загрузки списка баз данных (оставляем жестко закодированный список)
    private void LoadAvailableDatabases()
    {
        try
        {
            AddLogMessage("Загрузка списка баз данных...");
            AvailableDatabases.Clear();
            // Список баз данных - предполагаем, что он статичен
            var dbNames = new List<string> { 
                "notificationEF", "notificationZK", "notificationOK", 
                "fcsNotification", "contract", "purchaseNotice", "requestQuotation"
                // Добавьте другие базы данных сюда, если необходимо
            };
            
            foreach (var name in dbNames.OrderBy(n => n))
            {
                AvailableDatabases.Add(new DatabaseInfo { Name = name });
            }
            AddLogMessage($"Загружено {AvailableDatabases.Count} баз данных.");
            // SelectedDatabase = AvailableDatabases.FirstOrDefault(); // Можно выбрать первую по умолчанию
        }
        catch (Exception ex)
        {
            AddLogMessage($"ОШИБКА при загрузке списка баз данных: {ex.Message}");
            // Можно показать ошибку пользователю
        }
    }

    // Метод для загрузки списка тем из базы IAC (реализован синхронно)
    private void LoadAvailableThemes()
    {
        AddLogMessage("Загрузка списка тем...");
        AvailableThemes.Clear();
        if (string.IsNullOrEmpty(_iacConnectionString))
        { AddLogMessage("ОШИБКА: Строка подключения к базе IAC/zakupkiWeb не найдена."); return; }

        // Используем правильную таблицу dbo.theme и столбцы themeID, themeName
        string query = "SELECT themeID, themeName FROM dbo.theme ORDER BY themeName;"; 
        
        SqlConnection connection = null;
        try
        {
            connection = new SqlConnection(_iacConnectionString);
            using (SqlCommand command = new SqlCommand(query, connection))
            {
                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    if (!reader.HasRows) { AddLogMessage("Темы в таблице dbo.theme не найдены."); }
                    else
                    {
                        while (reader.Read())
                        {
                            try
                            {
                                // Используем правильные имена столбцов при чтении
                                int id = reader.GetInt32(reader.GetOrdinal("themeID"));
                                string name = reader.IsDBNull(reader.GetOrdinal("themeName")) ? "(Без имени)" : reader.GetString(reader.GetOrdinal("themeName"));
                                AvailableThemes.Add(new ThemeInfo { Id = id, Name = name });
                            }
                            catch (Exception readEx) { AddLogMessage($"ОШИБКА при чтении строки темы: {readEx.Message}"); }
                        }
                    }
                }
            }
            AddLogMessage($"Загружено {AvailableThemes.Count} тем.");
        }
        catch (SqlException sqlEx) { AddLogMessage($"ОШИБКА SQL при загрузке списка тем: {sqlEx.Message}"); }
        catch (Exception ex) { AddLogMessage($"ОБЩАЯ ОШИБКА при загрузке списка тем: {ex.Message}"); }
        finally { connection?.Close(); }
    }

    private void LoadConfigurationAndSettings()
    {
        try
        {
            _baseConnectionString = App.Configuration.GetConnectionString("DefaultConnection");
            _iacConnectionString = App.Configuration.GetConnectionString("IacConnection");

            // Создаем и заполняем объект настроек из конфигурации
            var settings = new ApplicationSettings
            {
                UserId = App.Configuration.GetValue<int>("AppSettings:UserId"),
                ProcessId = App.Configuration.GetValue<int>("AppSettings:ProcessId"),
                SleepIntervalMilliseconds = App.Configuration.GetValue<int>("AppSettings:SleepIntervalMilliseconds", 100),
                MaxParallelDownloads = App.Configuration.GetValue<int>("AppSettings:MaxParallelDownloads", 4),
                // Читаем настройки FTP (кроме пароля)
                FtpHost = App.Configuration.GetValue<string>("FtpSettings:Host"),
                FtpPort = App.Configuration.GetValue<int>("FtpSettings:Port", 21),
                FtpUsername = App.Configuration.GetValue<string>("FtpSettings:Username"),
                // FtpPassword = App.Configuration.GetValue<string>("FtpSettings:Password"), // НЕ ЧИТАЕМ ПАРОЛЬ ИЗ КОНФИГА
                FtpPassword = null, // Инициализируем null или пустой строкой
                FtpUseSsl = App.Configuration.GetValue<bool>("FtpSettings:UseSsl", false),
                FtpValidateCertificate = App.Configuration.GetValue<bool>("FtpSettings:ValidateCertificate", true)
            };
            
            if (settings.MaxParallelDownloads <= 0) settings.MaxParallelDownloads = 1; 
            if (settings.SleepIntervalMilliseconds < 0) settings.SleepIntervalMilliseconds = 0; 

            CurrentSettings = settings; 

            AddLogMessage($"Настройки загружены. Потоков: {CurrentSettings.MaxParallelDownloads}, Пауза: {CurrentSettings.SleepIntervalMilliseconds} мс");
        }
        catch (Exception configEx) // Используем переменную
        {
            string errorMsg = $"КРИТИЧЕСКАЯ ОШИБКА при чтении конфигурации: {configEx.Message}";
            FileLogger.Log(errorMsg);
            MessageBox.Show(errorMsg, "Ошибка конфигурации", MessageBoxButton.OK, MessageBoxImage.Error);
            CurrentSettings = new ApplicationSettings(); 
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
             if (StartDownloadCommand is RelayCommand rc) rc.RaiseCanExecuteChanged();
            
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
} 