// using DownloaderApp.Models; // Удаляем эту строку
using System.Collections.Generic;
using FileDownloader.Models;


namespace DownloaderApp.Interfaces
{
    /// <summary>
    /// Интерфейс для сервиса конфигурации приложения
    /// </summary>
    public interface IConfigurationService
    {
        /// <summary>
        /// Получает строку подключения к основной базе данных
        /// </summary>
        string GetBaseConnectionString();

        /// <summary>
        /// Получает строку подключения к IAC базе данных
        /// </summary>
        string GetIacConnectionString();

        /// <summary>
        /// Получает строку подключения к серверу офиса
        /// </summary>
        string GetServerOfficeConnectionString();

        /// <summary>
        /// Получает настройки приложения
        /// </summary>
        ApplicationSettings GetApplicationSettings();

        /// <summary>
        /// Получает настройки FTP
        /// </summary>
        FtpSettings GetFtpSettings();

        /// <summary>
        /// Сохраняет настройки приложения
        /// </summary>
        void SaveApplicationSettings(ApplicationSettings settings);

        /// <summary>
        /// Сохраняет настройки FTP
        /// </summary>
        void SaveFtpSettings(FtpSettings settings);
    }
} 