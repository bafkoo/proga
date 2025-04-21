using System;

namespace DownloaderApp.Models;

/// <summary>
/// Запись для хранения метаданных файла
/// </summary>
public record FileMetadataRecord(
    int DocumentMetaID,
    string Url,
    DateTime PublishDate,
    string ComputerName,
    string DirectoryName,
    int DocumentMetaPathID,
    string PathDirectory,
    string FlDocument,
    string Ftp,
    string FileNameFtp,
    string FileName,
    string ExpName,
    string DocDescription,
    object UrlID
); 