using System;
using System.IO;
using System.Collections.Generic; // Добавлено для List<string>
// using System.IO.Compression; // Больше не нужен для основной распаковки
using System.Threading.Tasks;
using NLog;
using SharpCompress.Archives; // Используем SharpCompress
using SharpCompress.Common;    // Для ExtractionOptions
using SharpCompress.Readers;   // Для ReaderOptions

namespace DownloaderApp.Services
{
    /// <summary>
    /// Сервис для работы с архивами, использующий SharpCompress.
    /// </summary>
    public class ArchiveService
    {
        private readonly Logger _logger;

        public ArchiveService(Logger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Распаковывает архив (автоматическое определение типа) в указанную директорию.
        /// </summary>
        /// <param name="archivePath">Путь к архиву.</param>
        /// <param name="destinationPath">Путь к директории назначения.</param>
        /// <param name="overwriteFiles">Перезаписывать ли существующие файлы.</param>
        /// <returns>Список полных путей к распакованным файлам.</returns>
        public List<string> ExtractArchive(string archivePath, string destinationPath, bool overwriteFiles = true)
        {
            if (!File.Exists(archivePath))
            {
                _logger.Error($"Архив не найден для распаковки: {archivePath}");
                throw new FileNotFoundException("Архив не найден.", archivePath);
            }

            _logger.Info($"Начало распаковки архива (автоопределение): {archivePath} -> {destinationPath}");
            var extractedFiles = new List<string>(); // Список для хранения путей

            try
            {
                // Убедимся, что директория назначения существует
                Directory.CreateDirectory(destinationPath);

                var readerOptions = new ReaderOptions { LookForHeader = true }; // Помогает определить тип
                var extractionOptions = new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = overwriteFiles
                };

                using (Stream stream = File.OpenRead(archivePath))
                using (var reader = ArchiveFactory.Open(stream, readerOptions)) // Автоопределение типа
                {
                    _logger.Info($"Тип архива определен как: {reader.Type}");
                    foreach (var entry in reader.Entries)
                    {
                        if (!entry.IsDirectory) // Распаковываем только файлы
                        {
                            string extractedFilePath = Path.Combine(destinationPath, entry.Key);
                            _logger.Debug($"Распаковка файла: {entry.Key} -> {extractedFilePath}");
                            entry.WriteToDirectory(destinationPath, extractionOptions);
                            extractedFiles.Add(extractedFilePath); // Добавляем путь к файлу в список
                        }
                    }
                }

                _logger.Info($"Архив успешно распакован: {archivePath}. Извлечено файлов: {extractedFiles.Count}");
                return extractedFiles; // Возвращаем список файлов
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Ошибка при распаковке архива: {archivePath}");
                throw; // Перебрасываем исключение дальше
            }
        }

        // Старый метод для ZIP можно удалить или оставить как алиас
        // public void ExtractZipArchive(string archivePath, string destinationPath, bool overwriteFiles = true)
        // {
        //     ExtractArchive(archivePath, destinationPath, overwriteFiles);
        // }

        // TODO: Добавить методы для других типов архивов (RAR и т.д.), если нужно
        // Это потребует подключения сторонних библиотек, например, SharpCompress.
    }
} 