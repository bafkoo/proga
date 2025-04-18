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
            for (int attempt = 1; attempt <= retryCount; attempt++)
            {
                try
                {
                    using (SqlCommand command = new SqlCommand(procedureName, connection) { CommandType = CommandType.StoredProcedure })
                    {
                        configureCommand(command);
                        await command.ExecuteNonQueryAsync(token);
                        return; // Успешное выполнение, выходим из метода
                    }
                }
                catch (SqlException ex) when (attempt < retryCount)
                {
                    // Логируем и ждем перед повторной попыткой
                    await fileLogger.LogInfoAsync($"Ошибка SQL при выполнении {procedureName}: {ex.Message}. Попытка {attempt} из {retryCount}.");
                    await Task.Delay(TimeSpan.FromSeconds(2), token); // Задержка перед повторной попыткой
                }
                catch (Exception ex)
                {
                    await fileLogger.LogInfoAsync($"Ошибка при выполнении {procedureName}: {ex.Message}");
                    throw; // Пробрасываем исключение дальше
                }
            }
        }
    }
} 