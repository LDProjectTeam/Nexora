namespace Nexora.Core.Models
{
    /// <summary>
    /// Описание установщика, который лаунчер собирается запустить. Передаётся в UI
    /// для явного подтверждения пользователем (запуск чужого setup.exe, UAC-повтор,
    /// установка системных компонентов из репака).
    /// </summary>
    public sealed record InstallerRunRequest(
        string GameId,
        string GameTitle,
        string InstallerPath,
        long FileSizeBytes,
        bool IsElevated,
        bool IsSilent,
        string Reason);
}
