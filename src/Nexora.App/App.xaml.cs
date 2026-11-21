using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Nexora.App.Services;
using Nexora.App.ViewModels;
using Nexora.Core.Interfaces;
using Nexora.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nexora.App
{
    public partial class App : Application
    {
        private IServiceProvider? _serviceProvider;
        private MainWindow? _mainWindow;
        private Mutex? _singleInstanceMutex;

        /// <summary>
        /// Application-wide service provider. Used by views that are created directly
        /// (for example the game details window opened from a card) to resolve
        /// optional metadata services without changing their construction sites.
        /// </summary>
        public static IServiceProvider? Services { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Only one launcher may drive the torrent engine at a time. A previous
            // instance usually needs just a moment to release the mutex during shutdown.
            _singleInstanceMutex = new Mutex(false, @"Local\Nexora.Launcher.SingleInstance");
            var ownsMutex = false;
            try
            {
                ownsMutex = _singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                Shutdown();
                return;
            }

            // Register legacy code pages (including Windows-1251) before
            // any services or application code may try to use them.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            DispatcherUnhandledException += (s, ev) =>
            {
                ev.Handled = true; // Don't crash the application on unhandled dispatcher exceptions
            };

            base.OnStartup(e);

            ShutdownMode = ShutdownMode.OnExplicitShutdown; // Only shutdown when explicitly told!

            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();
            Services = _serviceProvider;

            var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
            settingsService.Load();

            var savedLang = settingsService.Settings.IsLanguageManuallySelected &&
                (settingsService.Settings.Language is "ru" or "uk" or "en")
                ? settingsService.Settings.Language
                : (!string.IsNullOrWhiteSpace(settingsService.Settings.Language) && (settingsService.Settings.Language is "ru" or "uk" or "en"))
                    ? settingsService.Settings.Language
                    : LocalizationManager.GetSystemLanguage();
            LocalizationManager.SetLanguage(savedLang);

            // Core services produce user-facing text (torrent phases, errors, etc.).
            Nexora.Core.Services.Localizer.SetResolver(key => LocalizationManager.GetString(key));

            // Apply the saved accent color before the window renders.
            AccentOption.Apply(settingsService.Settings.AccentColor);

            _mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            MainWindow = _mainWindow;
            _mainWindow.Show();

            // Shrink the artwork cache in the background: converts artwork cached by
            // older versions into WebP, removes leftovers and enforces the size cap.
            _ = Task.Run(async () =>
            {
                try
                {
                    await ArtworkCacheMaintenance.RunAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Cache maintenance is best effort and must never affect startup.
                }
            });
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            // Logging. Debug output is only useful in a debugger or when the launcher
            // is started from the console script; a normal windowed start should not
            // pay for formatting debug/trace messages.
            var debugLogging = Debugger.IsAttached;
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(debugLogging ? LogLevel.Debug : LogLevel.Information);
                if (debugLogging || Console.IsOutputRedirected)
                    builder.AddConsole();
            });

            // Reuse pooled connections and allow bounded concurrency for searches/artwork.
            // ResponseHeadersRead downloads continue streaming after the headers arrive.
            services.AddSingleton(new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 16,
                ConnectTimeout = TimeSpan.FromSeconds(8)
            })
            {
                Timeout = TimeSpan.FromSeconds(20)
            });

            // Core Services
            services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<IGameCatalog, GameCatalogService>();
            services.AddSingleton<RqbitDownloader>();
            services.AddSingleton<HttpDownloader>();
            services.AddSingleton<IInstaller, Installer>();
            services.AddSingleton<IFileVerifier, FileVerifier>();
            services.AddSingleton<IUpdater, Updater>();
            services.AddSingleton<IDownloadManager, DownloadManager>();
            services.AddSingleton<IGameLauncher, GameLauncher>();

            // Search system
            services.AddSingleton<IDiskSpaceService, DiskSpaceService>();
            services.AddSingleton<ITitleNormalizer, TitleNormalizer>();
            services.AddSingleton<IVersionSelector, VersionSelector>();
            services.AddSingleton<IGameArtworkService, SteamArtworkService>();
            // IGameSource registrations — DI will inject IEnumerable<IGameSource> automatically
            services.AddSingleton<IGameSource, BuiltInCatalogSource>();   // priority 0
            services.AddSingleton<IGameSource, RutorGameSource>();         // priority 1
            services.AddSingleton<IGameSearchAggregator, GameSearchAggregator>();
            services.AddSingleton<ISteamFeaturedGamesService, SteamFeaturedGamesService>();
            services.AddSingleton<ISteamMetadataService, SteamMetadataService>();

            services.AddSingleton<IGameManager, GameManager>();

            // Notifications
            services.AddSingleton<NotificationService>();

            // ViewModels & Views
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_serviceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }

            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;

            base.OnExit(e);
        }
    }
}
