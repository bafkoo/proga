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

                var readerOptions = new ReaderOptions { LookForHeader = true, ArchiveEncoding = new SharpCompress.Common.ArchiveEncoding { Default = System.Text.Encoding.GetEncoding("cp866") } };
                var extractionOptions = new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = overwriteFiles
                };

                bool triedUtf8 = false;
            retryExtract:
                try
                {
                    using (Stream stream = File.OpenRead(archivePath))
                    using (var reader = ArchiveFactory.Open(stream, readerOptions))
                    {
                        _logger.Info($"Тип архива определен как: {reader.Type}");
                        foreach (var entry in reader.Entries)
                        {
                            if (!entry.IsDirectory)
                            {
                                string extractedFilePath = Path.Combine(destinationPath, entry.Key);
                                _logger.Debug($"Распаковка файла: {entry.Key} -> {extractedFilePath}");
                                entry.WriteToDirectory(destinationPath, extractionOptions);
                                extractedFiles.Add(extractedFilePath);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!triedUtf8)
                    {
                        _logger.Warn($"Fallback: повторная распаковка с кодировкой UTF8 для архива: {archivePath}");
                        readerOptions = new ReaderOptions { LookForHeader = true, ArchiveEncoding = new SharpCompress.Common.ArchiveEncoding { Default = System.Text.Encoding.UTF8 } };
                        triedUtf8 = true;
                        goto retryExtract;
                    }
                    _logger.Error(ex, $"Ошибка при распаковке архива: {archivePath}");
                    throw;
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

        /// <summary>
        /// Рекурсивно распаковывает архивы любого уровня вложенности и возвращает список всех файлов.
        /// </summary>
        /// <param name="archivePath">Путь к архиву.</param>
        /// <param name="destinationPath">Путь к директории назначения.</param>
        /// <param name="overwriteFiles">Перезаписывать ли существующие файлы.</param>
        /// <param name="maxDepth">Максимальная глубина рекурсии.</param>
        /// <returns>Список всех распакованных файлов (включая из вложенных архивов).</returns>
        public List<string> ExtractArchiveRecursive(string archivePath, string destinationPath, bool overwriteFiles = true, int maxDepth = 5)
        {
            var allExtractedFiles = new List<string>();
            ExtractArchiveRecursiveInternal(archivePath, destinationPath, overwriteFiles, 0, maxDepth, allExtractedFiles);
            return allExtractedFiles;
        }

        private void ExtractArchiveRecursiveInternal(string archivePath, string destinationPath, bool overwriteFiles, int currentDepth, int maxDepth, List<string> allExtractedFiles)
        {
            if (currentDepth > maxDepth)
            {
                _logger.Warn($"Достигнута максимальная глубина рекурсии ({maxDepth}) для архива: {archivePath}");
                return;
            }
            var extractedFiles = ExtractArchive(archivePath, destinationPath, overwriteFiles);
            foreach (var file in extractedFiles)
            {
                if (IsArchive(file))
                {
                    string nestedExtractDir = Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file));
                    Directory.CreateDirectory(nestedExtractDir);
                    _logger.Info($"Обнаружен вложенный архив: {file}. Начинаем рекурсивную распаковку...");
                    ExtractArchiveRecursiveInternal(file, nestedExtractDir, overwriteFiles, currentDepth + 1, maxDepth, allExtractedFiles);
                }
                else if (Directory.Exists(file))
                {
                    foreach (var nestedFile in Directory.GetFiles(file, "*", SearchOption.AllDirectories))
                    {
                        if (IsArchive(nestedFile))
                        {
                            string nestedExtractDir = Path.Combine(Path.GetDirectoryName(nestedFile), Path.GetFileNameWithoutExtension(nestedFile));
                            Directory.CreateDirectory(nestedExtractDir);
                            _logger.Info($"Обнаружен архив в папке: {nestedFile}. Начинаем рекурсивную распаковку...");
                            ExtractArchiveRecursiveInternal(nestedFile, nestedExtractDir, overwriteFiles, currentDepth + 1, maxDepth, allExtractedFiles);
                        }
                        else
                        {
                            allExtractedFiles.Add(nestedFile);
                        }
                    }
                }
                else
                {
                    allExtractedFiles.Add(file);
                }
            }
        }

        private static bool IsArchive(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext == ".zip" || ext == ".rar" || ext == ".7z" || ext == ".tar" || ext == ".gz" || ext == ".bz2";
        }
    }
} 