using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DownloaderApp.Services
{
    public class FileProcessingService
    {
        public async Task ProcessFileAsync(DataRow row, Func<int, CancellationToken, Task> updateDownloadFlag, CancellationToken token)
        {
            // Извлечение данных из строки
            int documentMetaID = Convert.ToInt32(row["documentMetaID"]);
            string fileName = row["fileName"].ToString();
            string directoryName = row["directoryName"].ToString();
            string computerName = row["computerName"].ToString();

            // Логика обработки файла
            string filePath = Path.Combine(computerName, directoryName, fileName);
            if (!File.Exists(filePath))
            {
                // Скачивание файла или другая логика
                // ...
            }

            // Обновление флага после успешной обработки
            await updateDownloadFlag(documentMetaID, token);
        }
    }
} 