using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using DownloaderApp.Infrastructure;
using DownloaderApp.Interfaces;
using DownloaderApp.Infrastructure.Logging;
using System.Collections.Generic;
using DownloaderApp.Models;

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
        /// <remarks>
        /// Этот метод является оберткой над InsertAttachmentAsync для совместимости,
        /// создавая AttachmentModel из переданных параметров.
        /// </remarks>
        public async Task<int> InsertAttachmentFromExtractedAsync(string connectionString, int n, string fileName, string docDescription, string url, long? fileSize, string expName, CancellationToken token)
        {
            await _fileLogger.LogInfoAsync($"InsertAttachmentFromExtractedAsync: Попытка добавить запись для извлеченного файла '{fileName}' (n={n}).");

            var attachment = new AttachmentModel
            {
                N = n,
                FileName = fileName,
                DocDescription = docDescription,
                Url = url,
                FileSize = fileSize?.ToString(),
            };

            return await InsertAttachmentAsync(connectionString, attachment, token);
        }

        /// <summary>
        /// Получает детали вложения из таблицы attachment по его ID.
        /// </summary>
        public async Task<AttachmentModel> GetAttachmentDetailsByIdAsync(string connectionString, int attachmentId, CancellationToken token)
        {
            await _fileLogger.LogDebugAsync($"GetAttachmentDetailsByIdAsync: Попытка получить детали для attachmentID: {attachmentId}");
            AttachmentModel attachment = null;

            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync(token);
                    // Запрашиваем все поля, необходимые для модели AttachmentModel (и для процедуры attachmentInsert)
                    string query = @"
                        SELECT 
                            dbID, n, attachments_Id, medicalCommissionDecision_Id, 
                            publishedContentId, fileName, fileSize, docDescription, 
                            url, contentId, docDate, content, 
                            notificationAttachments_Id, attachment_Id, 
                            unableProvideContractGuaranteeDocs_Id
                        FROM dbo.attachment 
                        WHERE attachmentID = @attachmentId";

                    using (SqlCommand cmd = new SqlCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@attachmentId", attachmentId);

                        using (SqlDataReader reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, token))
                        {
                            if (await reader.ReadAsync(token))
                            {
                                attachment = new AttachmentModel
                                {
                                    // Используем хелперы для безопасного чтения
                                    DbId = reader["dbID"] == DBNull.Value ? 0 : reader.GetInt32(reader.GetOrdinal("dbID")),
                                    N = reader["n"] == DBNull.Value ? 0 : reader.GetInt32(reader.GetOrdinal("n")),
                                    AttachmentsId = reader["attachments_Id"] == DBNull.Value ? (int?)null : reader.GetInt32(reader.GetOrdinal("attachments_Id")),
                                    MedicalCommissionDecisionId = reader["medicalCommissionDecision_Id"] == DBNull.Value ? (int?)null : reader.GetInt32(reader.GetOrdinal("medicalCommissionDecision_Id")),
                                    PublishedContentId = reader["publishedContentId"] == DBNull.Value ? null : reader.GetString(reader.GetOrdinal("publishedContentId")),
                                    FileName = reader["fileName"] == DBNull.Value ? null : reader.GetString(reader.GetOrdinal("fileName")),
                                    FileSize = reader["fileSize"] == DBNull.Value ? null : reader.GetString(reader.GetOrdinal("fileSize")), // fileSizе хранится как varchar?
                                    DocDescription = reader["docDescription"] == DBNull.Value ? null : reader.GetString(reader.GetOrdinal("docDescription")),
                                    Url = reader["url"] == DBNull.Value ? null : reader.GetString(reader.GetOrdinal("url")),
                                    ContentId = reader["contentId"] == DBNull.Value ? null : reader.GetString(reader.GetOrdinal("contentId")),
                                    DocDate = reader["docDate"] == DBNull.Value ? (DateTime?)null : reader.GetDateTime(reader.GetOrdinal("docDate")),
                                    Content = reader["content"] == DBNull.Value ? null : reader.GetString(reader.GetOrdinal("content")),
                                    NotificationAttachmentsId = reader["notificationAttachments_Id"] == DBNull.Value ? (int?)null : reader.GetInt32(reader.GetOrdinal("notificationAttachments_Id")),
                                    AttachmentId = reader["attachment_Id"] == DBNull.Value ? (int?)null : reader.GetInt32(reader.GetOrdinal("attachment_Id")),
                                    UnableProvideContractGuaranteeDocsId = reader["unableProvideContractGuaranteeDocs_Id"] == DBNull.Value ? (int?)null : reader.GetInt32(reader.GetOrdinal("unableProvideContractGuaranteeDocs_Id"))
                                };
                                await _fileLogger.LogSuccessAsync($"GetAttachmentDetailsByIdAsync: Детали для attachmentID: {attachmentId} успешно получены.");
                            }
                            else
                            {
                                await _fileLogger.LogWarningAsync($"GetAttachmentDetailsByIdAsync: Запись с attachmentID: {attachmentId} не найдена.");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await _fileLogger.LogErrorAsync($"GetAttachmentDetailsByIdAsync: Ошибка при получении деталей для attachmentID: {attachmentId}. {ex.Message}", ex);
                // Не пробрасываем ошибку, возвращаем null, чтобы вызывающий код мог решить, что делать
            }

            return attachment;
        }

        /// <summary>
        /// Добавляет запись о файле (обычном или извлеченном) в таблицу attachment.
        /// </summary>
        public async Task<int> InsertAttachmentAsync(string connectionString, AttachmentModel attachment, CancellationToken token)
        {
            await _fileLogger.LogInfoAsync($"InsertAttachmentAsync: Попытка добавить запись для файла '{attachment.FileName}' (n={attachment.N}).");
            try
            {
                int newAttachmentID = 0; // Инициализируем ID по умолчанию

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync(token);
                    await _fileLogger.LogDebugAsync($"InsertAttachmentAsync: Connection opened. DataSource='{connection.DataSource}', Database='{connection.Database}'");

                    // Предполагаем, что ExecuteProcedureAsync теперь может возвращать ID
                    // или мы модифицируем его позже. Пока что он возвращает Task.
                    // Нам нужно получить @newAttachmentID
                    using (SqlCommand cmd = new SqlCommand("dbo.attachmentInsert", connection))
                    {
                         cmd.CommandType = CommandType.StoredProcedure;

                         // --- ПРОВЕРИТЬ СООТВЕТСТВИЕ ПАРАМЕТРОВ --- 
                         cmd.Parameters.AddWithValue("@dbID", attachment.DbId); // << ДОБАВЛЕНО? УТОЧНИТЬ
                         cmd.Parameters.AddWithValue("@n", attachment.N);
                         cmd.Parameters.AddWithValue("@attachments_Id", attachment.AttachmentsId); // << ДОБАВЛЕНО? УТОЧНИТЬ
                         cmd.Parameters.AddWithValue("@medicalCommissionDecision_Id", attachment.MedicalCommissionDecisionId); // << ДОБАВЛЕНО? УТОЧНИТЬ
                         cmd.Parameters.AddWithValue("@publishedContentId", (object)attachment.PublishedContentId ?? DBNull.Value); // << ДОБАВЛЕНО? УТОЧНИТЬ
                         cmd.Parameters.AddWithValue("@fileName", attachment.FileName);
                         cmd.Parameters.AddWithValue("@fileSize", (object)attachment.FileSize ?? DBNull.Value);
                         cmd.Parameters.AddWithValue("@docDescription", (object)attachment.DocDescription ?? DBNull.Value);
                         cmd.Parameters.AddWithValue("@url", (object)attachment.Url ?? DBNull.Value);
                         cmd.Parameters.AddWithValue("@contentId", (object)attachment.ContentId ?? DBNull.Value); // << ДОБАВЛЕНО? УТОЧНИТЬ
                         cmd.Parameters.AddWithValue("@docDate", (object)attachment.DocDate ?? DBNull.Value); // << ДОБАВЛЕНО? УТОЧНИТЬ
                         cmd.Parameters.AddWithValue("@content", (object)attachment.Content ?? DBNull.Value); // << ДОБАВЛЕНО? УТОЧНИТЬ
                         cmd.Parameters.AddWithValue("@notificationAttachments_Id", attachment.NotificationAttachmentsId); // << ДОБАВЛЕНО? УТОЧНИТЬ
                         cmd.Parameters.AddWithValue("@attachment_Id", attachment.AttachmentId); // << ДОБАВЛЕНО? УТОЧНИТЬ
                         cmd.Parameters.AddWithValue("@unableProvideContractGuaranteeDocs_Id", attachment.UnableProvideContractGuaranteeDocsId); // << ДОБАВЛЕНО? УТОЧНИТЬ
                         
                         // ПАРАМЕТРЫ, КОТОРЫЕ БЫЛИ РАНЕЕ:
                         // cmd.Parameters.AddWithValue("@expName", (object)attachment.ExpName ?? DBNull.Value); // <<< ЭТОТ ПАРАМЕТР ОТСУТСТВУЕТ В ПРОЦЕДУРЕ? ЗАКОММЕНТИРОВАЛ
                         
                         // ВЫХОДНОЙ ПАРАМЕТР ПРОЦЕДУРЫ:
                         SqlParameter msgParam = new SqlParameter("@Msg", SqlDbType.VarChar, 500)
                         {
                             Direction = ParameterDirection.Output
                         };
                         cmd.Parameters.Add(msgParam); 

                         // БЫЛ ПАРАМЕТР @newAttachmentID, КОТОРЫЙ НЕ НУЖЕН ЭТОЙ ПРОЦЕДУРЕ?
                         // SqlParameter outputIdParam = new SqlParameter("@newAttachmentID", SqlDbType.Int)
                         // {
                         //     Direction = ParameterDirection.Output
                         // };
                         // cmd.Parameters.Add(outputIdParam);

                         await cmd.ExecuteNonQueryAsync(token); // Используем ExecuteNonQueryAsync напрямую

                         string procedureMessage = msgParam.Value as string;
                         if (!string.IsNullOrEmpty(procedureMessage))
                         {
                             await _fileLogger.LogWarningAsync($"InsertAttachmentAsync: Сообщение от процедуры для файла '{attachment.FileName}': {procedureMessage}");
                         }
                         else 
                         {
                             await _fileLogger.LogSuccessAsync($"InsertAttachmentAsync: Процедура для файла '{attachment.FileName}' выполнена успешно (нет сообщения от @Msg).");
                         }
                         
                         // Проверяем, было ли возвращено значение ID (если процедура его возвращает, чего пока нет)
                         // Пока что ID не возвращается процедурой attachmentInsert
                         // if (outputIdParam.Value != DBNull.Value && outputIdParam.Value != null)
                         // {
                         //     newAttachmentID = (int)outputIdParam.Value;
                         //     await _fileLogger.LogSuccessAsync($"InsertAttachmentAsync: Процедура для файла '{attachment.FileName}' выполнена. Новый ID: {newAttachmentID}.");
                         // }
                         // else
                         // {
                         //     await _fileLogger.LogWarningAsync($"InsertAttachmentAsync: Процедура для файла '{attachment.FileName}' выполнена, но не вернула ID.");
                         // }
                    }
                }
                // Т.к. процедура attachmentInsert не возвращает ID, возвращаем 0 или другое значение по умолчанию
                // Либо нужно модифицировать процедуру для возврата ID
                return 0; 
            }
            catch (SqlException sqlEx)
            {
                // Добавляем номер ошибки SQL в лог
                await _fileLogger.LogErrorAsync($"InsertAttachmentAsync: Ошибка SQL ({sqlEx.Number}) при добавлении записи для файла '{attachment.FileName}'. {sqlEx.Message}", sqlEx);
                throw; // Повторно выбрасываем исключение
            }
            catch (Exception ex)
            {
                await _fileLogger.LogErrorAsync($"InsertAttachmentAsync: Общая ошибка при добавлении записи для файла '{attachment.FileName}'. {ex.Message}", ex);
                throw;
            }
        }

    }
} 