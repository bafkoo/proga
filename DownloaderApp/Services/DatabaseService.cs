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
        /// Вставляет метаданные скачанного файла в IAC через процедуру documentMetaPathInsert.
        /// </summary>
        public async Task InsertDocumentMetaPathAsync(string iacConnectionString, IDictionary<string, object> parameters, CancellationToken token)
        {
            using (SqlConnection connection = new SqlConnection(iacConnectionString))
            {
                await connection.OpenAsync(token);
                await SqlProcedureExecutor.ExecuteProcedureAsync("documentMetaPathInsert", connection, cmd =>
                {
                    foreach (var param in parameters)
                    {
                        cmd.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
                    }
                }, token, _fileLogger);
            }
        }

        public async Task<string> InsertDocumentMetaPathArchiveAsync(string connectionString, IDictionary<string, object> parameters, CancellationToken token)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync(token);
                using (var cmd = new SqlCommand("documentMetaPathArchiveInsert", connection))
                {
                    cmd.CommandType = CommandType.StoredProcedure;

                    cmd.Parameters.AddWithValue("@documentMetaID", parameters["@documentMetaID"]);
                    cmd.Parameters.AddWithValue("@processID", parameters["@processID"]);
                    cmd.Parameters.AddWithValue("@urlID", parameters["@urlID"]);
                    cmd.Parameters.AddWithValue("@urlIDText", parameters["@urlIDText"]);
                    cmd.Parameters.AddWithValue("@fileName", parameters["@fileName"]);
                    cmd.Parameters.AddWithValue("@expName", parameters["@expName"]);
                    cmd.Parameters.AddWithValue("@fileSize", parameters["@fileSize"]);
                    cmd.Parameters.AddWithValue("@databaseName", parameters["@databaseName"]);

                    var outParam = new SqlParameter("@newFileName", System.Data.SqlDbType.VarChar, 250)
                    {
                        Direction = System.Data.ParameterDirection.Output
                    };
                    cmd.Parameters.Add(outParam);

                    await cmd.ExecuteNonQueryAsync(token);
                    return outParam.Value?.ToString() ?? string.Empty;
                }
            }
        }

        public async Task<int> InsertDocumentMetaPathAsyncWithId(string iacConnectionString, IDictionary<string, object> parameters, CancellationToken token)
        {
            using (SqlConnection connection = new SqlConnection(iacConnectionString))
            {
                await connection.OpenAsync(token);
                using (var cmd = new SqlCommand("documentMetaPathInsert", connection))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    foreach (var param in parameters)
                    {
                        cmd.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
                    }
                    var outParam = new SqlParameter("@documentMetaPathID", System.Data.SqlDbType.Int)
                    {
                        Direction = System.Data.ParameterDirection.Output
                    };
                    cmd.Parameters.Add(outParam);
                    await cmd.ExecuteNonQueryAsync(token);
                    return outParam.Value != DBNull.Value ? Convert.ToInt32(outParam.Value) : 0;
                }
            }
        }

    }
} 