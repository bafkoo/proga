using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using DownloaderApp.Infrastructure;
using DownloaderApp.Interfaces;
using DownloaderApp.Infrastructure.Logging;

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
            using (SqlConnection conBase = new SqlConnection(connectionString))
            {
                await conBase.OpenAsync(token);
                await SqlProcedureExecutor.ExecuteProcedureAsync("documentMetaUpdateFlag", conBase, cmd =>
                {
                    cmd.Parameters.AddWithValue("@documentMetaID", documentMetaID);
                }, token, _fileLogger, 3);
            }
        }
    }
} 