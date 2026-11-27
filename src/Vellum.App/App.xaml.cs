using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Vellum.App.Services;
using Vellum.App.ViewModels;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;
using Vellum.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vellum.App
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

            // A normal Bootstrapper launch proves itself with a one-time token over the
            // inherited standard-input pipe. Arguments and environment variables are
            // deliberately not trusted.
            var ipcToken = BootstrapperHandshake.TryReadFromStandardInput(TimeSpan.FromSeconds(5));
            var trustedLaunch = BootstrapperLaunchPolicy.IsTrustedLaunch(ipcToken, e.Args,
                Environment.GetEnvironmentVariable("VELLUM_BOOTSTRAPPED"));

#if DEBUG
            if (e.Args.Any(arg => string.Equals(arg, "--allow-direct", StringComparison.OrdinalIgnoreCase))
                || Debugger.IsAttached)
            {
                trustedLaunch = true;
            }
#endif

            if (!trustedLaunch)
            {
                var childOfBootstrapper = e.Args.Any(arg =>
                    string.Equals(arg, BootstrapperLaunchPolicy.ChildMarkerArgument,
                        StringComparison.OrdinalIgnoreCase));
                if (childOfBootstrapper)
                {
                    var choice = MessageBox.Show(
                        "Vellum Bootstrapper started this process, but its one-time launch " +
                        "confirmation was not received.\n\n" +
                        "Yes — start Vellum directly\nNo — close",
                        "Vellum launch error", MessageBoxButton.YesNo, MessageBoxImage.Error);
                    if (choice != MessageBoxResult.Yes)
                    {
                        Shutdown();
                        return;
                    }
                    AppLog.Warn("Startup",
                        "User explicitly started Vellum after a failed bootstrapper handshake.");
                }
                else
                {
                    var outcome = RunBootstrapperFallback(e.Args);
                    if (outcome is BootstrapperFallbackOutcome.ExitToBootstrapper
                        or BootstrapperFallbackOutcome.Close)
                    {
                        Shutdown();
                        return;
                    }

                    AppLog.Warn("Startup",
                        "User explicitly chose a direct Vellum start after the bootstrapper failed.");
                }
            }

            try
            {
                // Only one launcher may drive the torrent engine at a time. If the object
                // already existed, give the previous instance a short moment to finish a
                // shutdown; otherwise activate it immediately instead of waiting 3 seconds.
                _singleInstanceMutex = new Mutex(false, @"Local\Vellum.Launcher.SingleInstance", out var createdNew);
                var ownsMutex = false;
                try
                {
                    ownsMutex = _singleInstanceMutex.WaitOne(createdNew ? TimeSpan.Zero : TimeSpan.FromSeconds(1));
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                if (!ownsMutex)
                {
                    AppLog.Info("Startup", "Another launcher instance is running; asking it to show its window.");
                    SingleInstanceSignal.RequestActivation();
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
                    $"Vellum {typeof(App).Assembly.GetName().Version} starting. " +
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
                Vellum.Core.Services.Localizer.SetResolver(key => LocalizationManager.GetString(key));

                // Installer consent prompts are intentionally disabled: the setup from a
                // download starts immediately when the user resumes or installs a game.

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
                            LocalizationManager.GetStringOrFallback("StartupFailedFormat", "Could not start the launcher: {0}"),
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
        /// Fallback for launches that bypass the Bootstrapper shortcut. The main app
        /// hands control to Vellum.Bootstrapper.exe and exits before creating a window.
        /// </summary>
        private static BootstrapperFallbackOutcome RunBootstrapperFallback(string[] args)
        {
            var baseDir = AppContext.BaseDirectory;
            var bootstrapper = Path.Combine(baseDir, "Bootstrapper", "Vellum.Bootstrapper.exe");
            if (!File.Exists(bootstrapper))
                bootstrapper = Path.Combine(baseDir, "Vellum.Bootstrapper.exe");

            return BootstrapperFallbackCoordinator.Run(
                () =>
                {
                    if (!File.Exists(bootstrapper))
                    {
                        return new BootstrapperFallbackAttempt
                        {
                            Started = false,
                            ErrorMessage = "Vellum.Bootstrapper.exe was not found next to Vellum.exe."
                        };
                    }

                    var health = Path.Combine(Path.GetTempPath(),
                        "vellum-bootstrapper-" + Guid.NewGuid().ToString("N") + ".health");
                    Process? process = null;
                    try
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = bootstrapper,
                            WorkingDirectory = baseDir,
                            UseShellExecute = false
                        };
                        foreach (var arg in args)
                        {
                            if (string.Equals(arg, "--allow-direct", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(arg, BootstrapperLaunchPolicy.ChildMarkerArgument,
                                    StringComparison.OrdinalIgnoreCase)
                                || string.Equals(arg, BootstrapperLaunchPolicy.LegacyMarkerArgument,
                                    StringComparison.OrdinalIgnoreCase))
                                continue;
                            startInfo.ArgumentList.Add(arg);
                        }
                        startInfo.ArgumentList.Add("--bootstrapper-health");
                        startInfo.ArgumentList.Add(health);

                        process = Process.Start(startInfo);
                        if (process == null)
                        {
                            return new BootstrapperFallbackAttempt
                            {
                                Started = false,
                                ErrorMessage = "Vellum.Bootstrapper.exe did not start."
                            };
                        }

                        return new BootstrapperFallbackAttempt
                        {
                            Started = true,
                            HealthFile = health,
                            HasExited = () => process.HasExited,
                            Kill = () =>
                            {
                                try { if (!process.HasExited) process.Kill(); } catch { }
                            }
                        };
                    }
                    catch (Exception ex)
                    {
                        process?.Dispose();
                        return new BootstrapperFallbackAttempt
                        {
                            Started = false,
                            ErrorMessage = "Could not start Vellum.Bootstrapper.exe: " + ex.Message
                        };
                    }
                },
                BootstrapperFallbackCoordinator.DefaultHealthTimeout,
                message =>
                {
                    var choice = MessageBox.Show(
                        message + "\n\n" +
                        "Yes — retry the bootstrapper\n" +
                        "No — start Vellum directly\n" +
                        "Cancel — close",
                        "Vellum Bootstrapper error",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Error);
                    return choice switch
                    {
                        MessageBoxResult.Yes => BootstrapperFallbackChoice.Retry,
                        MessageBoxResult.No => BootstrapperFallbackChoice.StartDirectly,
                        _ => BootstrapperFallbackChoice.Close
                    };
                });
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

            // Manifest based game updates and launcher self-update.
            services.AddSingleton<IManifestSignatureVerifier, ManifestSignatureVerifier>();
            services.AddSingleton<IUpdateManifestProvider, HttpUpdateManifestProvider>();
            services.AddSingleton<IFileDiffService, FileDiffService>();
            services.AddSingleton<IIntegrityVerifier, IntegrityVerifier>();
            services.AddSingleton<IUpdateDownloadManager, UpdateDownloadManager>();
            services.AddSingleton<IUpdateStateStore, UpdateStateStore>();
            services.AddSingleton<IUpdateInstaller, UpdateInstaller>();
            services.AddSingleton<IGameUpdateService, GameUpdateService>();
            services.AddSingleton<ILauncherUpdateService, LauncherUpdateService>();

            // Notifications
            services.AddSingleton<NotificationService>();

            // Windows taskbar progress indicator (optional native integration)
            services.AddSingleton<ITaskbarProgressService, TaskbarProgressService>();

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
