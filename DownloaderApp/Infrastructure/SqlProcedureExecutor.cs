using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using DownloaderApp.Infrastructure.Logging;
using System.Linq;
using System.Collections.Generic;

namespace DownloaderApp.Infrastructure
{
    public static class SqlProcedureExecutor
    {
        public static async Task ExecuteProcedureAsync(string procedureName, SqlConnection connection, Action<SqlCommand> configureCommand, CancellationToken token, IFileLogger fileLogger, int retryCount = 3)
        {
            SqlException lastSqlException = null; // Сохраняем последнее исключение
            for (int attempt = 1; attempt <= retryCount; attempt++)
            {
                try
                {
                    using (SqlCommand command = new SqlCommand(procedureName, connection) { CommandType = CommandType.StoredProcedure })
                    {
                        configureCommand(command);
                        await command.ExecuteNonQueryAsync(token);
                        // Логируем успешное выполнение (опционально, может быть слишком много логов)
                        // await fileLogger.LogDebugAsync($"Процедура {procedureName} успешно выполнена (попытка {attempt}).");
                        return; // Успешное выполнение, выходим из метода
                    }
                }
                catch (SqlException ex)
                {
                    lastSqlException = ex; // Сохраняем исключение
                    if (attempt < retryCount)
                    {
                        // Логируем ОШИБКУ и ждем перед повторной попыткой
                        await fileLogger.LogErrorAsync($"Ошибка SQL при выполнении {procedureName}: {ex.Number} - {ex.Message}. Попытка {attempt} из {retryCount}. Повтор через 2 сек.", ex);
                        await Task.Delay(TimeSpan.FromSeconds(2), token);
                    }
                    else
                    {
                        // Логируем финальную ошибку перед выходом из цикла
                        await fileLogger.LogErrorAsync($"Ошибка SQL при выполнении {procedureName} после {retryCount} попыток: {ex.Number} - {ex.Message}.", ex);
                    }
                }
                catch (Exception ex)
                {
                    // Логируем любую другую ошибку
                    await fileLogger.LogErrorAsync($"Неперехваченная ошибка при выполнении {procedureName} (попытка {attempt}): {ex.Message}", ex);
                    throw; // Пробрасываем не-SQL исключение дальше немедленно
                }
            }

            // Если цикл завершился, значит все попытки провалились из-за SqlException
            throw new Exception($"Не удалось выполнить процедуру {procedureName} после {retryCount} попыток.", lastSqlException);
        }

        /// <summary>
        /// Пакетное обновление флагов загрузки для нескольких файлов
        /// </summary>
        /// <param name="connection">SQL соединение</param>
        /// <param name="documentMetaIDs">Список ID документов для обновления</param>
        /// <param name="token">Токен отмены</param>
        /// <param name="fileLogger">Логгер</param>
        /// <returns>Количество успешно обновленных записей</returns>
        public static async Task<int> BatchUpdateDownloadFlagsAsync(SqlConnection connection, IEnumerable<int> documentMetaIDs, CancellationToken token, IFileLogger fileLogger)
        {
            if (documentMetaIDs == null || !documentMetaIDs.Any())
                return 0;

            int updatedCount = 0;
            try
            {
                // Создаем временную таблицу для хранения списка ID
                using (SqlCommand createTempTableCmd = new SqlCommand(
                    "IF OBJECT_ID('tempdb..#TempDocumentIDs') IS NOT NULL DROP TABLE #TempDocumentIDs; " +
                    "CREATE TABLE #TempDocumentIDs (DocumentMetaID INT PRIMARY KEY);", connection))
                {
                    await createTempTableCmd.ExecuteNonQueryAsync(token);
                }

                // Заполняем временную таблицу списком ID
                using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection))
                {
                    bulkCopy.DestinationTableName = "#TempDocumentIDs";
                    
                    DataTable idTable = new DataTable();
                    idTable.Columns.Add("DocumentMetaID", typeof(int));
                    
                    foreach (int id in documentMetaIDs)
                    {
                        idTable.Rows.Add(id);
                    }
                    
                    await bulkCopy.WriteToServerAsync(idTable, token);
                }

                // Выполняем пакетное обновление
                using (SqlCommand batchUpdateCmd = new SqlCommand(
                    "UPDATE a SET a.downloadFlag = 1 " +
                    "FROM attachment a " +
                    "INNER JOIN #TempDocumentIDs t ON a.attachmentID = t.DocumentMetaID;", connection))
                {
                    updatedCount = await batchUpdateCmd.ExecuteNonQueryAsync(token);
                }

                await fileLogger.LogSuccessAsync($"Пакетное обновление флагов: обновлено {updatedCount} из {documentMetaIDs.Count()} записей.");
                
                return updatedCount;
            }
            catch (Exception ex)
            {
                await fileLogger.LogErrorAsync($"Ошибка при пакетном обновлении флагов: {ex.Message}", ex);
                throw;
            }
        }
    }
} 