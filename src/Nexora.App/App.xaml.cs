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
using Nexora.Core.Helpers;
using Nexora.Core.Interfaces;
using Nexora.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nexora.App
{
    public partial class App : Application
    {
        static App()
        {
            // App.xaml is parsed before OnStartup runs. A malformed resource dictionary
            // (for example a duplicate x:Key) used to kill the process with no trace at
            // all. Subscribe early so at least the log file records the exception.
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                AppLog.Fatal("Startup", "Unhandled exception (possibly while loading App.xaml).",
                    args.ExceptionObject as Exception);
                AppLog.Flush();
            };
        }

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
            InstallGlobalExceptionHandlers();

            try
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
                    AppLog.Info("Startup", "Another launcher instance is already running; exiting.");
                    Shutdown();
                    return;
                }

                // Register legacy code pages (including Windows-1251) before
                // any services or application code may try to use them.
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

                base.OnStartup(e);

                ShutdownMode = ShutdownMode.OnExplicitShutdown; // Only shutdown when explicitly told!

                var services = new ServiceCollection();
                ConfigureServices(services);
                _serviceProvider = services.BuildServiceProvider();
                Services = _serviceProvider;

                AppLog.Info("Startup",
                    $"Nexora {typeof(App).Assembly.GetName().Version} starting. " +
                    $"Data: {PathHelper.AppDataDir}; OS: {Environment.OSVersion.VersionString}");

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
                    catch (Exception ex)
                    {
                        AppLog.Warn("ArtworkCache", "Cache maintenance failed.", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                AppLog.Fatal("Startup", "The launcher failed to start.", ex);
                AppLog.Flush();

                try
                {
                    MessageBox.Show(
                        string.Format(
                            LocalizationManager.GetString("StartupFailedFormat", "Could not start the launcher: {0}"),
                            ex.Message),
                        LocalizationManager.GetString("CommonError", "Error"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch
                {
                    // Nothing left to show the message with.
                }

                Shutdown(1);
            }
        }

        /// <summary>
        /// Подписывается на все глобальные источники необработанных исключений.
        /// Без этого ошибка в фоновой задаче или UI-потоке исчезала бесследно.
        /// </summary>
        private void InstallGlobalExceptionHandlers()
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => AppLog.Flush();

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                AppLog.Error("TaskScheduler", "Unobserved task exception.", args.Exception);
                args.SetObserved();
            };

            DispatcherUnhandledException += (_, args) =>
            {
                ErrorReporter.Report("Dispatcher", args.Exception);
                args.Handled = true; // keep the launcher alive, but the failure is logged and shown
            };
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            // Logging. The file sink is always enabled so field failures leave a trace;
            // console output is only useful in a debugger or when started from a script.
            var debugLogging = Debugger.IsAttached;
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(debugLogging ? LogLevel.Debug : LogLevel.Information);
                builder.AddProvider(new FileLoggerProvider());
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
            AppLog.Info("Shutdown", $"Exiting with code {e.ApplicationExitCode}.");

            try
            {
                if (_serviceProvider is IDisposable disposable)
                    disposable.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Error("Shutdown", "Disposing services failed.", ex);
            }

            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;

            AppLog.Flush();
            base.OnExit(e);
        }
    }
}
