using System.Collections.Generic;
using DownloaderApp.Interfaces;

namespace FileDownloader.Models;

// Простой класс для хранения настроек, используемых в UI
public class ApplicationSettings
{
    public int UserId { get; set; }
    public int ProcessId { get; set; }
    public int SleepIntervalMilliseconds { get; set; }
    public int MaxParallelDownloads { get; set; } = 2; // Default value

    // Настройки FTP (можно добавить больше, если нужно)
    public string FtpHost { get; set; }
    public int FtpPort { get; set; } = 21;
    public string FtpUsername { get; set; }
    public string FtpPassword { get; set; } // Пароль хранится в памяти только на время работы
    public bool FtpUseSsl { get; set; } 
    public bool FtpValidateCertificate { get; set; } = true;

    // Настройки UI темы
    public string BaseTheme { get; set; } = "Light"; // Значение по умолчанию
    public string AccentColor { get; set; } = "Blue";  // Значение по умолчанию

    // Конструктор по умолчанию
    public ApplicationSettings() { }

    // Конструктор копирования (удобно для создания редактируемой копии)
    public ApplicationSettings(ApplicationSettings source)
    {
        UserId = source.UserId;
        ProcessId = source.ProcessId;
        SleepIntervalMilliseconds = source.SleepIntervalMilliseconds;
        MaxParallelDownloads = source.MaxParallelDownloads;
        FtpHost = source.FtpHost;
        FtpPort = source.FtpPort;
        FtpUsername = source.FtpUsername;
        FtpPassword = source.FtpPassword; // Копируем пароль (остается в памяти)
        FtpUseSsl = source.FtpUseSsl;
        FtpValidateCertificate = source.FtpValidateCertificate;
        // Копируем настройки темы
        BaseTheme = source.BaseTheme;
        AccentColor = source.AccentColor;
    }
} 