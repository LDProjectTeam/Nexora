using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Helpers;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;
using Microsoft.Extensions.Logging;

namespace Nexora.Core.Services
{
    public class GameLauncher : IGameLauncher
    {
        private readonly ILogger<GameLauncher> _logger;
        private readonly ISettingsService? _settingsService;
        private readonly SemaphoreSlim _launchLock = new(1, 1);
        private readonly ConcurrentDictionary<string, GameRunStatus> _statuses = new();
        private readonly ConcurrentDictionary<string, Process> _processes = new();

        public event EventHandler<GameRunStatus>? GameLaunched;
        public event EventHandler<GameRunStatus>? GameExited;

        public GameLauncher(ILogger<GameLauncher> logger)
            : this(logger, null)
        {
        }

        public GameLauncher(ILogger<GameLauncher> logger, ISettingsService? settingsService)
        {
            _logger = logger;
            _settingsService = settingsService;
        }

        public async Task<OperationResult> LaunchAsync(GameDefinition definition, GameInstallation installation)
        {
            await _launchLock.WaitAsync();
            try
            {
                if (IsRunning(definition.Id))
                {
                    _logger.LogWarning("Game {GameId} is already running.", definition.Id);
                    return OperationResult.Fail("Эта игра уже запущена.");
                }

                if (_settingsService?.Settings.AllowMultipleGames != true
                    && _statuses.Values.Any(status => status.State == GameRunState.Running
                        && !string.Equals(status.GameId, definition.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("Blocked launch of {GameId}: another game is already running.", definition.Id);
                    return OperationResult.Fail("Нельзя запустить игру, пока работает другая. Это ограничение можно отключить в настройках.");
                }

                return LaunchProcess(definition, installation);
            }
            finally
            {
                _launchLock.Release();
            }
        }

        private OperationResult LaunchProcess(GameDefinition definition, GameInstallation installation)
        {
            var exePath = installation.IsExternalShortcut
                ? installation.ExternalExecutablePath
                : string.IsNullOrWhiteSpace(definition.Executable)
                ? null
                : Path.Combine(installation.InstallPath, definition.Executable);
            if (!installation.IsExternalShortcut && (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)))
                exePath = GameExecutableLocator.FindMainExecutable(installation.InstallPath);

            if (exePath == null)
            {
                var setupPath = GameExecutableLocator.FindSetupExecutable(installation.InstallPath);
                if (setupPath != null)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = setupPath,
                            WorkingDirectory = Path.GetDirectoryName(setupPath) ?? installation.InstallPath,
                            UseShellExecute = true
                        });
                        _logger.LogWarning("No game executable found in {InstallPath}; opened installer {SetupPath}",
                            installation.InstallPath, setupPath);
                        return OperationResult.Ok();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Could not open installer {SetupPath}", setupPath);
                        return OperationResult.Fail($"Не удалось открыть установщик игры: {ex.Message}", ex);
                    }
                }

                _logger.LogError("No game executable found in {InstallPath}", installation.InstallPath);
                return OperationResult.Fail("Не удалось определить exe-файл игры.");
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = installation.IsExternalShortcut
                        ? installation.ExternalWorkingDirectory
                        : installation.InstallPath,
                    UseShellExecute = true
                };

                if (installation.IsExternalShortcut)
                    psi.Arguments = installation.ExternalLaunchArguments;
                else
                    foreach (var arg in definition.LaunchArguments)
                        psi.ArgumentList.Add(arg);

                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

                process.Exited += (_, _) => _ = Task.Run(() => HandleProcessExitedAsync(definition.Id, process));

                process.Start();
                _processes[definition.Id] = process;

                var runStatus = new GameRunStatus
                {
                    GameId = definition.Id,
                    State = GameRunState.Running,
                    StartedAt = DateTime.UtcNow
                };
                _statuses[definition.Id] = runStatus;
                GameLaunched?.Invoke(this, runStatus);

                _logger.LogInformation("Launched {GameName} (PID {Pid})", definition.Name, process.Id);
                return OperationResult.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to launch {GameId}", definition.Id);
                return OperationResult.Fail($"Не удалось запустить игру: {ex.Message}", ex);
            }
        }

        private async Task HandleProcessExitedAsync(string gameId, Process process)
        {
            await _launchLock.WaitAsync();
            try
            {
                var exitCode = process.ExitCode;
                var status = _statuses.GetOrAdd(gameId, id => new GameRunStatus { GameId = id });
                status.State = GameRunState.NotRunning;
                status.ExitCode = exitCode;
                status.EndedAt = DateTime.UtcNow;

                _logger.LogInformation("Game {GameId} exited with code {Code}.", gameId, exitCode);
                _processes.TryRemove(gameId, out _);
                GameExited?.Invoke(this, status);
                process.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update status after {GameId} exited.", gameId);
            }
            finally
            {
                _launchLock.Release();
            }
        }


        public GameRunStatus GetStatus(string gameId)
        {
            if (_statuses.TryGetValue(gameId, out var status))
                return status;
            return new GameRunStatus { GameId = gameId, State = GameRunState.NotRunning };
        }

        public bool IsRunning(string gameId)
            => _statuses.TryGetValue(gameId, out var s) && s.State == GameRunState.Running;
    }
}
