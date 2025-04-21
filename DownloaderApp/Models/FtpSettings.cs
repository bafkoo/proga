namespace DownloaderApp.Models;

public class FtpSettings
{
    public string FtpHost { get; set; }
    public int FtpPort { get; set; } = 21; // Default port
    public string FtpUsername { get; set; }
    public string FtpPassword { get; set; } // Stored separately/securely
    public bool FtpUseSsl { get; set; } = false;
    public bool FtpValidateCertificate { get; set; } = true;

    // Default constructor
    public FtpSettings() { }
} 