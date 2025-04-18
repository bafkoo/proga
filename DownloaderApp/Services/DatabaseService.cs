using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using DownloaderApp.Infrastructure;
using DownloaderApp.Interfaces;
using DownloaderApp.Infrastructure.Logging;
using System.Collections.Generic;

namespace DownloaderApp.Services
{
    public class DatabaseService
    {
        private readonly string _connectionString;
        private readonly IFileLogger _fileLogger;

        public DatabaseService(string connectionString, IFileLogger fileLogger)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _fileLogger = fileLogger ?? throw new ArgumentNullException(nameof(fileLogger));
        }

        public async Task<DataTable> FetchFileListAsync(DateTime dtB, DateTime dtE, int themeId, CancellationToken token)
        {
            DataTable dtTab = new DataTable();
            using (SqlConnection conBase = new SqlConnection(_connectionString))
            {
                await conBase.OpenAsync(token);
                // Создаем команду вручную, т.к. ExecuteProcedureAsync не возвращает результат
                using (SqlCommand cmd = new SqlCommand("documentMetaDownloadList", conBase))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.Parameters.AddWithValue("@themeID", themeId);
                    cmd.Parameters.AddWithValue("@dtB", dtB);
                    cmd.Parameters.AddWithValue("@dtE", dtE);
                    cmd.Parameters.AddWithValue("@srcID", 1); // Уточнить, нужен ли этот параметр здесь?

                    // Выполняем команду и получаем SqlDataReader
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token))
                    {
                        dtTab.Load(reader);
                    }
                }
            }
            return dtTab;
        }

        public async Task UpdateDownloadFlagAsync(string connectionString, int documentMetaID, CancellationToken token)
        {
            await _fileLogger.LogInfoAsync($"UpdateDownloadFlagAsync: Попытка обновить флаг для ID: {documentMetaID} через процедуру..."); 
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync(token);
                    await _fileLogger.LogDebugAsync($"UpdateDownloadFlagAsync: Connection opened. DataSource='{connection.DataSource}', Database='{connection.Database}'");
                    
                    await SqlProcedureExecutor.ExecuteProcedureAsync("documentMetaUpdateFlag", connection, cmd =>
                    {
                        cmd.Parameters.AddWithValue("@documentMetaID", documentMetaID);
                    }, token, _fileLogger);
                }
                await _fileLogger.LogSuccessAsync($"UpdateDownloadFlagAsync: Вызов процедуры documentMetaUpdateFlag для ID: {documentMetaID} завершен БЕЗ ИСКЛЮЧЕНИЙ.");
            }
            catch (Exception ex)
            {
                await _fileLogger.LogCriticalAsync($"UpdateDownloadFlagAsync: КРИТИЧЕСКАЯ ОШИБКА при вызове процедуры для ID: {documentMetaID}. Ошибка: {ex.ToString()}");
                throw;
            }
        }

        /// <summary>
        /// Добавляет запись об извлеченном из архива файле в таблицу attachment.
        /// </summary>
        public async Task<int> InsertExtractedAttachmentAsync(string connectionString, int n, string fileName, string docDescription, string url, long? fileSize, string expName, CancellationToken token)
        {
            await _fileLogger.LogInfoAsync($"InsertExtractedAttachmentAsync: Попытка добавить запись для файла '{fileName}' (n={n}).");
            try
            {
                int newAttachmentID = 0;
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync(token);
                    await SqlProcedureExecutor.ExecuteProcedureAsync("InsertExtractedAttachment", connection, cmd =>
                    {
                        cmd.Parameters.AddWithValue("@n", n);
                        cmd.Parameters.AddWithValue("@fileName", fileName);
                        cmd.Parameters.AddWithValue("@docDescription", (object)docDescription ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@url", (object)url ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@fileSize", (object)fileSize ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@expName", expName);
                        
                        // Добавляем выходной параметр
                        SqlParameter outputIdParam = new SqlParameter("@newAttachmentID", SqlDbType.Int)
                        {
                            Direction = ParameterDirection.Output
                        };
                        cmd.Parameters.Add(outputIdParam);

                    }, token, _fileLogger);
                    
                    // Получаем значение выходного параметра ПОСЛЕ выполнения процедуры
                    // Проверка, был ли параметр добавлен и вернул ли значение
                    if (connection.State == ConnectionState.Open) // Убедимся что соединение еще открыто
                    {
                       using (SqlCommand cmdGetOutput = new SqlCommand("SELECT @newAttachmentID", connection))
                       {
                           // Копируем параметр из предыдущей команды
                           var param = ((SqlCommand)connection.CreateCommand()).Parameters.AddWithValue("@newAttachmentID", SqlDbType.Int); 
                           param.Direction = ParameterDirection.Output; // Устанавливаем как Output
                           // Попытка получить значение (может не сработать напрямую после ExecuteProcedureAsync)
                           // Вместо этого, ExecuteProcedureAsync должен был бы возвращать ID
                           // Пока просто логируем, что процедура выполнена
                       } 
                    }
                      // Т.к. ExecuteProcedureAsync не возвращает значения, временно возвращаем 0 
                      // Позже нужно будет модифицировать ExecuteProcedureAsync или использовать другой подход
                      // newAttachmentID = (int)cmd.Parameters["@newAttachmentID"].Value; // Не сработает с текущим ExecuteProcedureAsync
                      
                }
                await _fileLogger.LogSuccessAsync($"InsertExtractedAttachmentAsync: Процедура для файла '{fileName}' выполнена успешно.");
                // Возвращаем 0, т.к. текущий ExecuteProcedureAsync не возвращает ID
                return 0; 
            }
            catch (Exception ex)
            {
                await _fileLogger.LogErrorAsync($"InsertExtractedAttachmentAsync: Ошибка при добавлении записи для файла '{fileName}'. {ex.Message}", ex);
                throw;
            }
        }

    }
} 