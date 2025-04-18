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
            // Добавляем детальное логирование
            await _fileLogger.LogInfoAsync($"UpdateDownloadFlagAsync: Попытка обновить флаг для ID: {documentMetaID} в строке: {connectionString?.Substring(0, connectionString.IndexOf(';') > 0 ? connectionString.IndexOf(';') : connectionString.Length)}..."); // Логируем только часть строки для безопасности
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync(token);
                    using (SqlCommand command = new SqlCommand("UPDATE attachment SET downloadFlag = 1 WHERE attachmentID = @ID", connection))
                    {
                        command.Parameters.AddWithValue("@ID", documentMetaID);
                        await command.ExecuteNonQueryAsync(token);
                    }
                }
                // Логируем успех, если SQL-запрос выполнен успешно
                await _fileLogger.LogSuccessAsync($"UpdateDownloadFlagAsync: Прямое обновление флага для ID: {documentMetaID} выполнено успешно.");
            }
            catch (Exception ex)
            {
                // Логируем критическую ошибку, если что-то пошло не так
                await _fileLogger.LogCriticalAsync($"UpdateDownloadFlagAsync: КРИТИЧЕСКАЯ ОШИБКА при обновлении флага для ID: {documentMetaID}. Ошибка: {ex.ToString()}");
                throw; // Пробрасываем исключение дальше
            }
        }

        /// <summary>
        /// Пакетное обновление флагов загрузки для нескольких файлов
        /// </summary>
        /// <param name="connectionString">Строка подключения к БД</param>
        /// <param name="documentMetaIDs">Список ID документов</param>
        /// <param name="token">Токен отмены</param>
        /// <returns>Количество обновленных записей</returns>
        public async Task<int> BatchUpdateDownloadFlagsAsync(string connectionString, List<int> documentMetaIDs, CancellationToken token)
        {
            if (documentMetaIDs == null || documentMetaIDs.Count == 0)
                return 0;

            await _fileLogger.LogInfoAsync($"BatchUpdateDownloadFlagsAsync: Попытка пакетного обновления {documentMetaIDs.Count} флагов");
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync(token);
                    int updatedCount = await SqlProcedureExecutor.BatchUpdateDownloadFlagsAsync(
                        connection, documentMetaIDs, token, _fileLogger);
                    
                    await _fileLogger.LogSuccessAsync($"BatchUpdateDownloadFlagsAsync: Пакетное обновление выполнено успешно. Обновлено {updatedCount} из {documentMetaIDs.Count} записей.");
                    return updatedCount;
                }
            }
            catch (Exception ex)
            {
                await _fileLogger.LogErrorAsync($"BatchUpdateDownloadFlagsAsync: Ошибка при пакетном обновлении. {ex.Message}", ex);
                throw;
            }
        }
    }
} 