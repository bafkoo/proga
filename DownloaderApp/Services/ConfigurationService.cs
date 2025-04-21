using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using DownloaderApp.Interfaces;
using DownloaderApp.Models;
using DownloaderApp.Services;


namespace DownloaderApp.Services
{
    /// <summary>
    /// Сервис для работы с конфигурацией приложения
    /// </summary>
    public class ConfigurationService : IConfigurationService
    {
        private readonly IConfiguration _configuration;

        /// <summary>
        /// Инициализирует новый экземпляр класса ConfigurationService
        /// </summary>
        public ConfigurationService()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            _configuration = builder.Build();
        }

        /// <summary>
        /// Получает строку подключения к основной базе данных
        /// </summary>
        public string GetBaseConnectionString()
        {
            return _configuration.GetConnectionString("BaseConnection") ?? 
                throw new InvalidOperationException("Не найдена строка подключения 'BaseConnection' в конфигурации");
        }

        /// <summary>
        /// Получает строку подключения к IAC базе данных
        /// </summary>
        public string GetIacConnectionString()
        {
            return _configuration.GetConnectionString("IacConnection") ?? 
                throw new InvalidOperationException("Не найдена строка подключения 'IacConnection' в конфигурации");
        }

        /// <summary>
        /// Получает строку подключения к серверу офиса
        /// </summary>
        public string GetServerOfficeConnectionString()
        {
            return _configuration.GetConnectionString("ServerOfficeConnection") ?? 
                throw new InvalidOperationException("Не найдена строка подключения 'ServerOfficeConnection' в конфигурации");
        }

        /// <summary>
        /// Получает настройки приложения
        /// </summary>
        public ApplicationSettings GetApplicationSettings()
        {
            var appSettings = new ApplicationSettings();
            _configuration.GetSection("ApplicationSettings").Bind(appSettings);
            return appSettings;
        }

        /// <summary>
        /// Получает настройки FTP
        /// </summary>
        public FtpSettings GetFtpSettings()
        {
            var ftpSettings = new FtpSettings();
            _configuration.GetSection("FtpSettings").Bind(ftpSettings);
            return ftpSettings;
        }

        /// <summary>
        /// Сохраняет настройки приложения
        /// </summary>
        public void SaveApplicationSettings(ApplicationSettings settings)
        {
            // Здесь должна быть логика сохранения настроек в файл конфигурации
            // или в другое постоянное хранилище
        }

        /// <summary>
        /// Сохраняет настройки FTP
        /// </summary>
        public void SaveFtpSettings(FtpSettings settings)
        {
            // Здесь должна быть логика сохранения настроек в файл конфигурации
            // или в другое постоянное хранилище
        }
    }
} 