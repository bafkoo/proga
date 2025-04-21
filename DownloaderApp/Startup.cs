using Microsoft.Extensions.DependencyInjection;
using DownloaderApp.Interfaces;
using DownloaderApp.Services;
using DownloaderApp.Infrastructure.Logging;

namespace DownloaderApp
{
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IDocumentDownloadService>(sp =>
            {
                var configService = sp.GetRequiredService<ConfigurationService>();
                var dbService = sp.GetRequiredService<DatabaseService>();
                var logger = sp.GetRequiredService<IFileLogger>();
                var httpClientService = sp.GetRequiredService<IHttpClientService>();
                var fcsConn = configService.GetServerOfficeConnectionString();
                var iacConn = configService.GetIacConnectionString();
                return new DocumentDownloadService(dbService, logger, httpClientService, fcsConn, iacConn);
            });
        }
    }
} 