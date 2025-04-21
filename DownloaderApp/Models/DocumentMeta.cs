namespace DownloaderApp.Models;

/// <summary>
/// Represents metadata required for archiving files via ArchiveService.
/// Ensure the properties here match the data available and needed by 
/// the documentMetaPathArchiveInsert stored procedure called by ArchiveService.
/// </summary>
public class DocumentMeta
{
    /// <summary>
    /// Corresponds to @documentMetaPathID parameter in the stored procedure.
    /// Needs to be populated before calling ArchiveService.
    /// </summary>
    public int documentMetaPathID { get; set; }

    /// <summary>
    /// Corresponds to @urlID parameter in the stored procedure.
    /// Can be int or potentially string/object depending on source.
    /// </summary>
    public object urlID { get; set; } // Use object to match DataRow, conversion handled in ArchiveService

    /// <summary>
    /// Corresponds to @documentMetaID parameter in the stored procedure.
    /// </summary>
    public int documentMetaID { get; set; }

    /// <summary>
    /// Corresponds to @processID parameter in the stored procedure.
    /// Often comes from application settings.
    /// </summary>
    public int processID { get; set; }

    /// <summary>
    /// Corresponds to @databaseName parameter in the stored procedure.
    /// Identifies the source database/context.
    /// </summary>
    public string databaseName { get; set; }

    // Add any other properties from the original DataRow/context 
    // that might be needed for constructing meta objects or 
    // for subscribers of the FileArchived event.
    // Example:
    // public DateTime PublishDate { get; set; } 
} 