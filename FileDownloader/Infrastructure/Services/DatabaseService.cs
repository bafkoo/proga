using FileDownloader.Infrastructure.Services;
using FileDownloader.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging; // Assuming ILogger is used via DI
using System;
using System.Data;
using System.Threading.Tasks;

namespace FileDownloader.Infrastructure.Services;

public partial class DatabaseService : IDatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(IConfiguration configuration, ILogger<DatabaseService> logger)
    {
        // Get connection string from appsettings.json
        // Use "ServerOfficeConnection" as it points to the fcsNotification database
        _connectionString = configuration.GetConnectionString("ServerOfficeConnection") 
                           ?? throw new InvalidOperationException("Connection string 'ServerOfficeConnection' not found.");
        _logger = logger;
    }

    public async Task<string?> InsertAttachmentAsync(AttachmentModel attachment)
    {
        string? errorMessage = null;
        const string procedureName = "dbo.attachmentInsert";

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await using var command = new SqlCommand(procedureName, connection)
            {
                CommandType = CommandType.StoredProcedure
            };

            // Add parameters - match names and types with the stored procedure
            // Use DBNull.Value for null C# values
            command.Parameters.AddWithValue("@dbID", attachment.DbID);
            command.Parameters.AddWithValue("@n", attachment.N);
            command.Parameters.AddWithValue("@attachments_Id", (object?)attachment.AttachmentsId ?? DBNull.Value);
            command.Parameters.AddWithValue("@medicalCommissionDecision_Id", (object?)attachment.MedicalCommissionDecisionId ?? DBNull.Value);
            command.Parameters.AddWithValue("@publishedContentId", (object?)attachment.PublishedContentId ?? DBNull.Value);
            command.Parameters.AddWithValue("@fileName", attachment.FileName);
            command.Parameters.AddWithValue("@fileSize", (object?)attachment.FileSize ?? DBNull.Value); // Consider SqlDbType.BigInt if type changes
            command.Parameters.AddWithValue("@docDescription", (object?)attachment.DocDescription ?? DBNull.Value);
            command.Parameters.AddWithValue("@url", attachment.Url);
            command.Parameters.AddWithValue("@contentId", (object?)attachment.ContentId ?? DBNull.Value);
            command.Parameters.AddWithValue("@docDate", (object?)attachment.DocDate ?? DBNull.Value);
            command.Parameters.AddWithValue("@content", (object?)attachment.Content ?? DBNull.Value);
            command.Parameters.AddWithValue("@notificationAttachments_Id", (object?)attachment.NotificationAttachmentsId ?? DBNull.Value);
            command.Parameters.AddWithValue("@attachment_Id", (object?)attachment.AttachmentId ?? DBNull.Value); // Potential redundancy?
            command.Parameters.AddWithValue("@unableProvideContractGuaranteeDocs_Id", (object?)attachment.UnableProvideContractGuaranteeDocsId ?? DBNull.Value);

            // Add the output parameter
            var msgParam = new SqlParameter("@Msg", SqlDbType.VarChar, 500)
            {
                Direction = ParameterDirection.Output
            };
            command.Parameters.Add(msgParam);

            await connection.OpenAsync();
            await command.ExecuteNonQueryAsync();

            // Retrieve the output parameter value
            if (msgParam.Value != DBNull.Value && msgParam.Value is string msg)
            {
                errorMessage = msg;
                _logger.LogWarning("Stored procedure {ProcedureName} returned a message: {Message}", procedureName, errorMessage);
            }
        }
        catch (SqlException ex)
        {
            errorMessage = $"SQL Error executing {procedureName}: {ex.Message}";
            _logger.LogError(ex, "Error executing stored procedure {ProcedureName}", procedureName);
            // Consider throwing a custom exception or re-throwing depending on desired handling
        }
        catch (Exception ex)
        {
            errorMessage = $"General Error executing {procedureName}: {ex.Message}";
            _logger.LogError(ex, "Error executing stored procedure {ProcedureName}", procedureName);
            // Consider throwing
        }

        return errorMessage; // Return null on success, or the error message on failure/SP message
    }

    // Implementation of other IDatabaseService methods...
} 