using System.Threading.Tasks;
using Nexora.Core.Models;

namespace Nexora.Core.Interfaces
{
    /// <summary>
    /// Reads and writes application settings.
    /// </summary>
    public interface ISettingsService
    {
        AppSettings Settings { get; }
        /// <summary>True если это первый запуск (настройки не были заданы).</summary>
        bool IsFirstRun { get; }
        void Load();
        Task LoadAsync();
        void Save();
        Task SaveAsync();
        void Reset();
    }
}
