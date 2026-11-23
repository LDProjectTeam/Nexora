namespace Vellum.Core.Models
{
    /// <summary>
    /// Type of game distribution release.
    /// Priority order: Portable -> Installer / Archive / Repack -> Unknown.
    /// </summary>
    public enum GameVersionType
    {
        /// <summary>Pre-extracted portable DRM-free build, requires no installer setup.</summary>
        Portable = 1,

        /// <summary>Official or GOG installer.</summary>
        Installer = 2,

        /// <summary>Compressed archive (zip, 7z, rar).</summary>
        Archive = 3,

        /// <summary>Repack release (FitGirl, DODI, Xatab, etc.) requiring extraction and installation.</summary>
        Repack = 4,

        /// <summary>Unspecified distribution type.</summary>
        Unknown = 5
    }
}
