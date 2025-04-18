using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using DownloaderApp.Infrastructure.Logging;

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
    }
} 