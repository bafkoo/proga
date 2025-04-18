using System;

namespace FileDownloader.Models;

public class LogMessage
{
    public string Message { get; set; }
    public string Type { get; set; }
    public DateTime Timestamp { get; set; }
    public string? FilePath { get; set; }
} 