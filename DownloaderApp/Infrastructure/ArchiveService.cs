using System;
using System.Data;
// using System.Data.SqlClient;
using Microsoft.Data.SqlClient; // Changed to Microsoft
using System.IO;
using FileDownloader.Models;

namespace DownloaderApp.Infrastructure
{
    // TODO: Ensure DocumentMeta class is defined or imported.
    public class DocumentMeta
    {
        public int documentMetaPathID { get; set; }
        public string databaseName { get; set; }
        public string urlIDText { get; set; }
        public int urlID { get; set; }
        public int documentMetaID { get; set; }
        public int processID { get; set; }
        // Add other necessary properties based on your definition
    }

    // TODO: Ensure BranchConnectionData class is defined or imported.
    public static class BranchConnectionData // Assuming static based on usage
    {
        public static string DataSource { get; set; } // Example property
        public static string UserID { get; set; }     // Example property
        public static string Password { get; set; }     // Example property
        // Add other necessary properties based on your definition
    }

    // Event arguments for the FileArchived event
    public class FileArchivedEventArgs : EventArgs
    {
        public string OriginalPath { get; }
        public string NewPath { get; }
        public string NewFileName { get; }
        public FileDownloader.Models.DocumentMeta DocumentMetadata { get; }
        public long FileSize { get; }

        public FileArchivedEventArgs(string originalPath, string newPath, string newFileName, FileDownloader.Models.DocumentMeta documentMetadata, long fileSize)
        {
            OriginalPath = originalPath;
            NewPath = newPath;
            NewFileName = newFileName;
            DocumentMetadata = documentMetadata;
            FileSize = fileSize;
        }
    }

    public class ArchiveService
    {
        private readonly string _iacConnectionString;

        // Constructor accepts the IAC connection string
        public ArchiveService(string iacConnectionString)
        {
            if (string.IsNullOrWhiteSpace(iacConnectionString))
            {
                throw new ArgumentNullException(nameof(iacConnectionString), "IAC connection string cannot be null or empty.");
            }
            _iacConnectionString = iacConnectionString;
        }

        // Event raised after a file is successfully archived and registered
        public event EventHandler<FileArchivedEventArgs> FileArchived;

        // Method to raise the FileArchived event
        protected virtual void OnFileArchived(FileArchivedEventArgs e)
        {
            FileArchived?.Invoke(this, e);
        }

        // Made public for accessibility, adjust if needed
        public void ArchiveFileMove(string srcDirectory, string dstDirectory, FileDownloader.Models.DocumentMeta documentMeta)
        {
            DirectoryInfo dir = new DirectoryInfo(srcDirectory);

            // Create destination directory if it doesn't exist
            if (!Directory.Exists(dstDirectory))
            {
                try
                {
                    Directory.CreateDirectory(dstDirectory);
                }
                catch (Exception ex)
                {
                    // Log or handle directory creation error
                    Console.WriteLine($"Error creating destination directory {dstDirectory}: {ex.Message}");
                    throw; // Re-throw or handle appropriately
                }
            }

            foreach (FileInfo files in dir.GetFiles())
            {
                string originalFullPath = files.FullName; // Store original full path
                string expName = files.Extension;
                string fileName = files.Name;
                string newFileName = "";
                long fileSize = files.Length;

                // Construct full destination path before checking/deleting
                string destFilePathWithName = Path.Combine(dstDirectory, files.Name);
                if (File.Exists(destFilePathWithName))
                {
                    File.Delete(destFilePathWithName);
                }

                SavePathArchive(documentMeta, fileName, expName, fileSize, out newFileName);

                // Ensure newFileName is not empty or null before proceeding
                if (string.IsNullOrEmpty(newFileName))
                {
                     // Log error or handle case where newFileName wasn't returned
                     Console.WriteLine($"Error: newFileName is empty for original file {fileName}. Skipping move and registration.");
                     continue; // Skip to the next file
                }


                string destFilePathWithNewName = Path.Combine(dstDirectory, newFileName);
                if (File.Exists(destFilePathWithNewName))
                {
                    File.Delete(destFilePathWithNewName);
                }

                // Use Path.Combine for robustness
                string sourceFilePath = files.FullName; // Use FullName for source in recursive calls
                try
                {
                    File.Move(sourceFilePath, destFilePathWithNewName);

                    // Raise the event after successful move
                    OnFileArchived(new FileArchivedEventArgs(
                        originalPath: sourceFilePath,
                        newPath: destFilePathWithNewName,
                        newFileName: newFileName,
                        documentMetadata: documentMeta,
                        fileSize: fileSize));
                }
                catch (IOException ex)
                {
                    // Handle potential IO errors during move (e.g., access denied, file in use)
                    Console.WriteLine($"Error moving file {sourceFilePath} to {destFilePathWithNewName}: {ex.Message}");
                    // Decide if you need to re-throw or handle differently
                    // Potentially skip this file and continue with others?
                }
                catch (Exception ex) // Catch other potential exceptions
                {
                     Console.WriteLine($"An unexpected error occurred during processing file {sourceFilePath}: {ex.Message}");
                     throw; // Re-throw unexpected errors
                }
            }

            foreach (DirectoryInfo prmDirectory in dir.GetDirectories())
            {
                // Recursive call - Pass the sub-directory full path
                // Pass the same destination directory and document meta
                ArchiveFileMove(prmDirectory.FullName, dstDirectory, documentMeta);
            }
        }

        // Made private as it seems to be a helper for ArchiveFileMove
        private void SavePathArchive(FileDownloader.Models.DocumentMeta documentMeta, string fileName, string expName, long fileSize, out string newFileName)
        {
            newFileName = ""; // Initialize out parameter
            #region Подключение к БД
            // Use using statement for automatic disposal of SqlConnection
            using (SqlConnection conBase = GetConnection())
            {
                try
                {
                    conBase.Open();
                    #endregion

                    #region Создание хранимых процедур
                    // Use using statement for automatic disposal of SqlCommand
                    using (SqlCommand cmdDocumentMetaPathArchiveInsert = new SqlCommand("documentMetaPathArchiveInsert", conBase))
                    {
                        cmdDocumentMetaPathArchiveInsert.CommandType = CommandType.StoredProcedure;
                        #endregion

                        #region Параметры
                        // Add parameters more concisely
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@documentMetaPathID", SqlDbType.Int).Value = documentMeta.documentMetaPathID;
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@urlID", SqlDbType.Int).Value = GetUrlIdAsInt(documentMeta.urlID); // Handle potential type difference
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@documentMetaID", SqlDbType.Int).Value = documentMeta.documentMetaID;
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@processID", SqlDbType.Int).Value = documentMeta.processID;
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@fileName", SqlDbType.VarChar, 250).Value = fileName;
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@expName", SqlDbType.VarChar, 10).Value = expName.TrimStart('.'); // Pass extension without leading dot?
                        // Cast long to int, assuming file size will never exceed Int32.MaxValue as per user guarantee.
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@fileSize", SqlDbType.Int).Value = (int)fileSize;
                        // Pass databaseName from DocumentMeta
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add("@databaseName", SqlDbType.VarChar, 50).Value = documentMeta.databaseName ?? (object)DBNull.Value; // Handle potential null

                        SqlParameter prmNewFileName = new SqlParameter("@newFileName", SqlDbType.VarChar, 250)
                        {
                            Direction = ParameterDirection.Output
                        };
                        cmdDocumentMetaPathArchiveInsert.Parameters.Add(prmNewFileName);
                        #endregion

                        cmdDocumentMetaPathArchiveInsert.CommandTimeout = 300;
                        cmdDocumentMetaPathArchiveInsert.ExecuteNonQuery();

                        // Check if output parameter is null or DBNull before accessing Value
                        if (prmNewFileName.Value != null && prmNewFileName.Value != DBNull.Value)
                        {
                            newFileName = prmNewFileName.Value.ToString();
                        }
                        else
                        {
                            // Handle case where stored procedure might not return the new file name
                            Console.WriteLine("Warning: @newFileName output parameter was null or DBNull.");
                        }
                    }
                }
                catch (SqlException ex) // More specific exception type
                {
                    Console.WriteLine($"SQL Error in SavePathArchive: {ex.Message}");
                    throw;
                }
                catch (Exception ex) // Catch other potential exceptions
                {
                     Console.WriteLine($"General Error in SavePathArchive: {ex.Message}");
                     throw;
                }
                // No need to explicitly close connection due to using statement
            }
        }

        // Helper method to create connection using the stored connection string
        private SqlConnection GetConnection()
        {
             return new SqlConnection(_iacConnectionString);
        }

        // Helper to handle potential UrlID type mismatch (object from DB vs int expected by SP)
        private object GetUrlIdAsInt(object urlIdFromMeta)
        {
            if (urlIdFromMeta == null || urlIdFromMeta == DBNull.Value)
            {
                return DBNull.Value;
            }
            if (urlIdFromMeta is int intValue)
            {
                return intValue;
            }
            if (int.TryParse(urlIdFromMeta.ToString(), out int parsedValue))
            {
                return parsedValue;
            }
            // Could not parse as int, return DBNull or throw?
            Console.WriteLine($"Warning: Could not parse urlID '{urlIdFromMeta}' as int for SavePathArchive.");
            return DBNull.Value; 
        }
    }
} 