using System;

namespace FileDownloader.Models;

/// <summary>
/// Represents the data for an attachment to be inserted into the database.
/// Matches parameters of the attachmentInsert stored procedure.
/// </summary>
public class AttachmentModel
{
    public int DbID { get; set; }
    public int N { get; set; } // Consider a more descriptive name if possible
    public int? AttachmentsId { get; set; } // Nullable if it can be optional
    public int? MedicalCommissionDecisionId { get; set; } // Nullable
    public string? PublishedContentId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? FileSize { get; set; } // Consider using long if storing bytes
    public string? DocDescription { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? ContentId { get; set; }
    public DateTime? DocDate { get; set; }
    public string? Content { get; set; }
    public int? NotificationAttachmentsId { get; set; } // Nullable
    public int? AttachmentId { get; set; } // Nullable (seems redundant with AttachmentsId?)
    public int? UnableProvideContractGuaranteeDocsId { get; set; } // Nullable
} 