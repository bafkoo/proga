using FileDownloader.Models; // Предполагаем, что есть модель AttachmentModel
using System.Threading.Tasks;

namespace FileDownloader.Infrastructure.Services;

/// <summary>
/// Service for interacting with the application database.
/// </summary>
public interface IDatabaseService
{
    /// <summary>
    /// Inserts attachment metadata into the database using the attachmentInsert stored procedure.
    /// </summary>
    /// <param name="attachment">The attachment data to insert.</param>
    /// <returns>A task representing the asynchronous operation, containing an error message if any.</returns>
    Task<string?> InsertAttachmentAsync(AttachmentModel attachment);

    // Add other database methods here as needed
} 