using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using DownloaderApp.Interfaces;
using DownloaderApp.Models;
using DownloaderApp.Infrastructure.Logging;

namespace DownloaderApp.Services;

public class DocumentDownloadService : IDocumentDownloadService
{
    private readonly DatabaseService _dbService;
    private readonly IFileLogger _logger;
    private readonly IHttpClientService _httpClientService;
    private readonly string _fcsConnection;
    private readonly string _iacConnection;

    public DocumentDownloadService(DatabaseService dbService, IFileLogger logger, IHttpClientService httpClientService, string fcsConnection, string iacConnection)
    {
        _dbService = dbService;
        _logger = logger;
        _httpClientService = httpClientService;
        _fcsConnection = fcsConnection;
        _iacConnection = iacConnection;
    }

    public async Task DownloadAndRegisterFilesAsync(DateTime dtB, DateTime dtE, int themeId, CancellationToken token)
    {
        var files = await _dbService.FetchFileListAsync(dtB, dtE, themeId, token);
        foreach (DataRow row in files.Rows)
        {
            int documentMetaID = Convert.ToInt32(row["documentMetaID"]);
            string url = row["url"].ToString();
            string pathDirectory = row.Table.Columns.Contains("pathDirectory") ? row["pathDirectory"].ToString() : null;
            string fileName = row["fileName"].ToString();
            string fileExtension = System.IO.Path.GetExtension(fileName);
            string newFileName = $"{documentMetaID}{fileExtension}";
            if (string.IsNullOrEmpty(pathDirectory)) continue;
            string savePath = System.IO.Path.Combine(pathDirectory, newFileName);
            try
            {
                var result = await _httpClientService.DownloadFileAsync(url, savePath, token);
                if (!result.Success)
                {
                    await _logger.LogErrorAsync($"Ошибка скачивания файла {url} -> {savePath}: {result.ErrorMessage}");
                    continue;
                }
            }
            catch (Exception ex)
            {
                await _logger.LogErrorAsync($"Ошибка скачивания файла {url} -> {savePath}: {ex.Message}", ex);
                continue;
            }
            // 2. Обновление флага
            await _dbService.UpdateDownloadFlagAsync(_fcsConnection, documentMetaID, token);
            // 3. Регистрация метаданных в IAC
            var parameters = new Dictionary<string, object>
            {
                {"@databaseName", "fcsNotification"},
                {"@computerName", row["computerName"]},
                {"@directoryName", row["directoryName"]},
                {"@themeID", themeId},
                {"@year", ((DateTime)row["publishDate"]).Year},
                {"@month", ((DateTime)row["publishDate"]).Month},
                {"@day", ((DateTime)row["publishDate"]).Day},
                {"@urlID", row["urlID"]},
                {"@urlIDText", row["urlID"]?.ToString() ?? string.Empty},
                {"@documentMetaID", documentMetaID},
                {"@processID", 0}, // Можно заменить на актуальный
                {"@fileName", row["fileName"]},
                {"@suffixName", string.Empty},
                {"@expName", row["expName"]},
                {"@docDescription", row["docDescription"]},
                {"@fileSize", 0}, // Можно заменить на актуальный
                {"@srcID", 0},
                {"@usrID", 0},
                {"@documentMetaPathID", 0}
            };
            await _dbService.InsertDocumentMetaPathAsync(_iacConnection, parameters, token);
        }
    }
} 