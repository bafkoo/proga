using System;
using System.Data;
// using System.Data.SqlClient;
using Microsoft.Data.SqlClient; // Changed to Microsoft
using System.IO;
using FileDownloader.Models;

namespace DownloaderApp.Infrastructure
{
    // TODO: Ensure DocumentMeta class is defined or imported.
    public class DocumentMeta
    {
        public int documentMetaPathID { get; set; }
        public string databaseName { get; set; }
        public string urlIDText { get; set; }
        public int urlID { get; set; }
        public int documentMetaID { get; set; }
        public int processID { get; set; }
        // Add other necessary properties based on your definition
    }

    // TODO: Ensure BranchConnectionData class is defined or imported.
    public static class BranchConnectionData // Assuming static based on usage
    {
        public static string DataSource { get; set; } // Example property
        public static string UserID { get; set; }     // Example property
        public static string Password { get; set; }     // Example property
        // Add other necessary properties based on your definition
    }

    // Event arguments for the FileArchived event
    public class FileArchivedEventArgs : EventArgs
    {
        public string OriginalPath { get; }
        public string NewPath { get; }
        public string NewFileName { get; }
        public FileDownloader.Models.DocumentMeta DocumentMetadata { get; }
        public long FileSize { get; }

        public FileArchivedEventArgs(string originalPath, string newPath, string newFileName, FileDownloader.Models.DocumentMeta documentMetadata, long fileSize)
        {
            OriginalPath = originalPath;
            NewPath = newPath;
            NewFileName = newFileName;
            DocumentMetadata = documentMetadata;
            FileSize = fileSize;
        }
    }

    public class ArchiveService
    {
        private readonly string _iacConnectionString;
        private const int MAX_RECURSION_DEPTH = 10; // Максимальная глубина рекурсии для обработки вложенных папок

        // Constructor accepts the IAC connection string
        public ArchiveService(string iacConnectionString)
        {
            if (string.IsNullOrWhiteSpace(iacConnectionString))
            {
                throw new ArgumentNullException(nameof(iacConnectionString), "IAC connection string cannot be null or empty.");
            }
            _iacConnectionString = iacConnectionString;
        }

        // Event raised after a file is successfully archived and registered
        public event EventHandler<FileArchivedEventArgs> FileArchived;

        // Method to raise the FileArchived event
        protected virtual void OnFileArchived(FileArchivedEventArgs e)
        {
            FileArchived?.Invoke(this, e);
        }

        // Безопасная проверка, достаточно ли свободного места на диске
        private bool EnsureDiskSpace(string dstDirectory, long requiredSpace)
        {
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(dstDirectory));
                if (driveInfo.IsReady && driveInfo.AvailableFreeSpace < requiredSpace)
                {
                    FileLogger.Log($"Предупреждение: На диске недостаточно свободного места. Требуется: {requiredSpace} байт, доступно: {driveInfo.AvailableFreeSpace} байт");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"Ошибка при проверке свободного места на диске: {ex.Message}");
                return true; // Продолжаем в случае ошибки проверки
            }
        }

        // Made public for accessibility, adjust if needed
        public void ArchiveFileMove(string srcDirectory, string dstDirectory, FileDownloader.Models.DocumentMeta documentMeta)
        {
            ArchiveFileMove(srcDirectory, dstDirectory, documentMeta, 0);
        }

        // Приватная перегрузка с отслеживанием глубины рекурсии
        private void ArchiveFileMove(string srcDirectory, string dstDirectory, FileDownloader.Models.DocumentMeta documentMeta, int recursionDepth)
        {
            if (recursionDepth > MAX_RECURSION_DEPTH)
            {
                FileLogger.Log($"Предупреждение: Достигнута максимальная глубина рекурсии ({MAX_RECURSION_DEPTH}). Пропуск дальнейшей обработки вложенных папок в {srcDirectory}");
                return;
            }

            // Проверка на недопустимые пути
            if (string.IsNullOrWhiteSpace(srcDirectory) || string.IsNullOrWhiteSpace(dstDirectory))
            {
                FileLogger.Log($"Ошибка: Некорректные пути для архивации. Исходный: {srcDirectory}, Целевой: {dstDirectory}");
                throw new ArgumentException("Исходный или целевой путь не указан");
            }

            // Защита от некорректных путей (например, системных)
            string[] protectedPaths = { "C:\\Windows", "C:\\Program Files", "C:\\Program Files (x86)" };
            foreach (var path in protectedPaths)
            {
                if (dstDirectory.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                {
                    FileLogger.Log($"Ошибка: Попытка записи в системную директорию {dstDirectory} отклонена");
                    throw new UnauthorizedAccessException($"Запись в системную директорию {dstDirectory} запрещена");
                }
            }

            DirectoryInfo dir;
            try
            {
                dir = new DirectoryInfo(srcDirectory);
                if (!dir.Exists)
                {
                    FileLogger.Log($"Ошибка: Исходная директория {srcDirectory} не существует");
                    throw new DirectoryNotFoundException($"Исходная директория {srcDirectory} не найдена");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"Ошибка при доступе к исходной директории {srcDirectory}: {ex.Message}");
                throw;
            }

            // Create destination directory if it doesn't exist
            if (!Directory.Exists(dstDirectory))
            {
                try
                {
                    Directory.CreateDirectory(dstDirectory);
                    FileLogger.Log($"Создана целевая директория: {dstDirectory}");
                }
                catch (Exception ex)
                {
                    // Log or handle directory creation error
                    FileLogger.Log($"Ошибка создания целевой директории {dstDirectory}: {ex.Message}");
                    throw; // Re-throw or handle appropriately
                }
            }

            // Оценка общего размера файлов
            long totalSize = 0;
            try
            {
                foreach (FileInfo file in dir.GetFiles())
                {
                    totalSize += file.Length;
                }
                
                // Проверка достаточности места на диске
                if (!EnsureDiskSpace(dstDirectory, totalSize))
                {
                    throw new IOException($"Недостаточно места на диске для перемещения {totalSize} байт");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"Ошибка при расчете требуемого дискового пространства: {ex.Message}");
                // Продолжаем выполнение без проверки места
            }

            foreach (FileInfo files in dir.GetFiles())
            {
                string originalFullPath = files.FullName; // Store original full path
                string expName = files.Extension;
                string fileName = files.Name;
                string newFileName = "";
                long fileSize = files.Length;

                // Проверка на корректность имени файла
                if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    // Обработка некорректных имен файлов - заменяем недопустимые символы
                    string safeFileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
                    FileLogger.Log($"Предупреждение: Имя файла '{fileName}' содержит недопустимые символы. Использую безопасное имя: '{safeFileName}'");
                    fileName = safeFileName;
                }

                // Construct full destination path before checking/deleting
                string destFilePathWithName = Path.Combine(dstDirectory, files.Name);
                if (File.Exists(destFilePathWithName))
                {
                    try
                    {
                        File.Delete(destFilePathWithName);
                        FileLogger.Log($"Удален существующий файл: {destFilePathWithName}");
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"Не удалось удалить существующий файл {destFilePathWithName}: {ex.Message}");
                        continue; // Пропускаем этот файл и переходим к следующему
                    }
                }

                try
                {
                    SavePathArchive(documentMeta, fileName, expName, fileSize, out newFileName);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"Ошибка при регистрации файла {fileName} в базе данных: {ex.Message}");
                    continue; // Пропускаем этот файл и переходим к следующему
                }

                // Ensure newFileName is not empty or null before proceeding
                if (string.IsNullOrEmpty(newFileName))
                {
                     // Log error or handle case where newFileName wasn't returned
                     FileLogger.Log($"Ошибка: Не получено новое имя файла для {fileName}. Пропуск файла.");
                     continue; // Skip to the next file
                }

                string destFilePathWithNewName = Path.Combine(dstDirectory, newFileName);
                if (File.Exists(destFilePathWithNewName))
                {
                    try
                    {
                        File.Delete(destFilePathWithNewName);
                        FileLogger.Log($"Удален существующий файл с новым именем: {destFilePathWithNewName}");
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"Не удалось удалить существующий файл {destFilePathWithNewName}: {ex.Message}");
                        continue; // Пропускаем этот файл и переходим к следующему
                    }
                }

                // Use Path.Combine for robustness
                string sourceFilePath = files.FullName; // Use FullName for source in recursive calls
                try
                {
                    File.Move(sourceFilePath, destFilePathWithNewName);
                    FileLogger.Log($"Файл перемещен: {sourceFilePath} -> {destFilePathWithNewName}");

                    // Raise the event after successful move
                    OnFileArchived(new FileArchivedEventArgs(
                        originalPath: sourceFilePath,
                        newPath: destFilePathWithNewName,
                        newFileName: newFileName,
                        documentMetadata: documentMeta,
                        fileSize: fileSize));
                }
                catch (IOException ex)
                {
                    // Handle potential IO errors during move (e.g., access denied, file in use)
                    FileLogger.Log($"Ошибка при перемещении файла {sourceFilePath} в {destFilePathWithNewName}: {ex.Message}");
                    
                    // Пробуем скопировать и удалить вместо перемещения
                    try
                    {
                        File.Copy(sourceFilePath, destFilePathWithNewName, true);
                        File.Delete(sourceFilePath);
                        FileLogger.Log($"Файл скопирован и исходный удален: {sourceFilePath} -> {destFilePathWithNewName}");
                        
                        // Raise the event after successful copy+delete
                        OnFileArchived(new FileArchivedEventArgs(
                            originalPath: sourceFilePath,
                            newPath: destFilePathWithNewName,
                            newFileName: newFileName,
                            documentMetadata: documentMeta,
                            fileSize: fileSize));
                    }
                    catch (Exception copyEx)
                    {
                        FileLogger.Log($"Не удалось скопировать файл {sourceFilePath} в {destFilePathWithNewName}: {copyEx.Message}");
                    }
                }
                catch (Exception ex) // Catch other potential exceptions
                {
                     FileLogger.Log($"Непредвиденная ошибка при обработке файла {sourceFilePath}: {ex.Message}");
                     // Продолжаем с другими файлами
                }
            }

            // Обработка вложенных директорий с отслеживанием глубины рекурсии
            foreach (DirectoryInfo prmDirectory in dir.GetDirectories())
            {
                // Recursive call - Pass the sub-directory full path
                // Pass the same destination directory and document meta
                try
                {
                    ArchiveFileMove(prmDirectory.FullName, dstDirectory, documentMeta, recursionDepth + 1);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"Ошибка при обработке вложенной директории {prmDirectory.FullName}: {ex.Message}");
                    // Продолжаем с другими директориями
                }
            }
        }

        // Made private as it seems to be a helper for ArchiveFileMove
        private void SavePathArchive(FileDownloader.Models.DocumentMeta documentMeta, string fileName, string expName, long fileSize, out string newFileName)
        {
            newFileName = ""; // Initialize out parameter
            #region Подключение к БД
            // Use using statement for automatic disposal of SqlConnection
            using (SqlConnection conBase = GetConnection())
            {
                try
                {
                    conBase.Open();
                    #endregion

                    #region Создание хранимых процедур
                    // Use using statement for automatic disposal of SqlCommand
                    using (SqlCommand cmdDocumentMetaPathArchiveInsert = new SqlCommand("documentMetaPathArchiveInsert", conBase))
                    {
                        cmdDocumentMetaPathArchiveInsert.CommandType = CommandType.StoredProcedure;
                        #endregion

                        #region Параметры
                        // Add parameters more concisely
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@documentMetaPathID", SqlDbType.Int).Value = documentMeta.documentMetaPathID;
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@urlID", SqlDbType.Int).Value = GetUrlIdAsInt(documentMeta.urlID); // Handle potential type difference
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@documentMetaID", SqlDbType.Int).Value = documentMeta.documentMetaID;
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@processID", SqlDbType.Int).Value = documentMeta.processID;
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@fileName", SqlDbType.VarChar, 250).Value = fileName;
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@expName", SqlDbType.VarChar, 10).Value = expName.TrimStart('.'); // Pass extension without leading dot?
                        // Cast long to int, assuming file size will never exceed Int32.MaxValue as per user guarantee.
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@fileSize", SqlDbType.Int).Value = (int)fileSize;
                        // Pass databaseName from DocumentMeta
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@databaseName", SqlDbType.VarChar, 50).Value = documentMeta.databaseName ?? (object)DBNull.Value; // Handle potential null

                        SqlParameter prmNewFileName = new SqlParameter("@newFileName", SqlDbType.VarChar, 250)
                        {
                            Direction = ParameterDirection.Output
                        };
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add(prmNewFileName);
                        #endregion

                        cmdDocumentMetaPathArchiveInsert.CommandTimeout = 300;
                        cmdDocumentMetaPathArchiveInsert.ExecuteNonQuery();

                        // Check if output parameter is null or DBNull before accessing Value
                        if (prmNewFileName.Value != null && prmNewFileName.Value != DBNull.Value)
                        {
                            newFileName = prmNewFileName.Value.ToString();
                        }
                        else
                        {
                            // Handle case where stored procedure might not return the new file name
                            FileLogger.Log("Предупреждение: Хранимая процедура не вернула новое имя файла");
                        }
                    }
                }
                catch (SqlException ex) // More specific exception type
                {
                    FileLogger.Log($"Ошибка SQL в SavePathArchive: {ex.Message}");
                    throw;
                }
                catch (Exception ex) // Catch other potential exceptions
                {
                     FileLogger.Log($"Общая ошибка в SavePathArchive: {ex.Message}");
                     throw;
                }
                // No need to explicitly close connection due to using statement
            }
        }

        // Helper method to create connection using the stored connection string
        private SqlConnection GetConnection()
        {
             return new SqlConnection(_iacConnectionString);
        }

        // Helper to handle potential UrlID type mismatch (object from DB vs int expected by SP)
        private object GetUrlIdAsInt(object urlIdFromMeta)
        {
            if (urlIdFromMeta == null || urlIdFromMeta == DBNull.Value)
            {
                return DBNull.Value;
            }
            if (urlIdFromMeta is int intValue)
            {
                return intValue;
            }
            if (int.TryParse(urlIdFromMeta.ToString(), out int parsedValue))
            {
                return parsedValue;
            }
            return DBNull.Value; // Возвращаем DBNull, если не удалось преобразовать
        }
    }
} 