using System;
using System.Threading.Tasks;
using Nexora.Core.Models;

namespace Nexora.Core.Interfaces
{
    public enum GameRunState { NotRunning, Running, Crashed }

    public class GameRunStatus
    {
        public string GameId { get; set; } = string.Empty;
        public GameRunState State { get; set; }
        public int? ExitCode { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
    }

    /// <summary>
    /// Launches a game process and tracks its lifetime.
    /// </summary>
    public interface IGameLauncher
    {
        /// <summary>Launch the game. Returns after the process starts.</summary>
        Task<OperationResult> LaunchAsync(GameDefinition definition, GameInstallation installation);

        /// <summary>Get current run status for a game.</summary>
        GameRunStatus GetStatus(string gameId);

        /// <summary>Is the game currently running?</summary>
        bool IsRunning(string gameId);

        /// <summary>Raised when a game process is launched.</summary>
        event EventHandler<GameRunStatus>? GameLaunched;

        /// <summary>Raised when a game process exits.</summary>
        event EventHandler<GameRunStatus>? GameExited;
    }
}
