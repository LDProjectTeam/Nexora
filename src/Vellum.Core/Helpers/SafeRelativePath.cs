using System;
using System.Collections.Generic;
using System.IO;

namespace Vellum.Core.Helpers
{
    /// <summary>
    /// Resolves manifest file paths safely. A remote manifest must never be able to
    /// write, read or delete anything outside the game/launcher root (path traversal,
    /// rooted paths, drive-relative paths, NTFS alternate data streams, device names).
    /// </summary>
    public static class SafeRelativePath
    {
        private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// Returns a normalized relative path using the host separator, or null when the
        /// path is unsafe. The result is guaranteed not to escape <paramref name="root"/>.
        /// </summary>
        public static string? NormalizeRelative(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return null;

            var path = relativePath.Trim().Replace('\\', '/');
            if (path.Length == 0 || path.Length > 1024)
                return null;

            // Absolute, drive-relative, UNC and device paths are never allowed.
            if (path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
                return null;
            if (path.Length >= 2 && path[1] == ':')
                return null;

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return null;

            foreach (var part in parts)
            {
                if (part is "." or "..")
                    return null;
                if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return null;
                if (part.Contains(':', StringComparison.Ordinal))
                    return null;
                if (part.EndsWith(' ') || part.EndsWith('.'))
                    return null;

                var stem = part;
                var dot = stem.IndexOf('.');
                if (dot >= 0)
                    stem = stem[..dot];
                if (ReservedNames.Contains(stem))
                    return null;
            }

            return Path.Combine(parts);
        }

        /// <summary>
        /// Resolves <paramref name="relativePath"/> under <paramref name="root"/> and
        /// verifies that the result stays inside it.
        /// </summary>
        public static bool TryResolve(string root, string? relativePath, out string fullPath)
        {
            fullPath = string.Empty;
            var normalized = NormalizeRelative(relativePath);
            if (normalized == null || string.IsNullOrWhiteSpace(root))
                return false;

            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(fullRoot, normalized));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }

            if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            fullPath = candidate;
            return true;
        }

        /// <summary>Throws when a manifest path is unsafe. Used at the trust boundary.</summary>
        public static string RequireNormalized(string? relativePath, string context)
        {
            var normalized = NormalizeRelative(relativePath);
            if (normalized == null)
                throw new InvalidDataException($"Unsafe file path in {context}: '{relativePath}'.");
            return normalized;
        }
    }

    /// <summary>Version/build comparison used to decide whether a manifest is an update.</summary>
    public static class UpdateVersion
    {
        public static bool IsSupportedSchema(int schemaVersion)
            => schemaVersion == Models.UpdateManifest.CurrentSchemaVersion;

        /// <summary>True when <paramref name="candidate"/> is newer than <paramref name="installed"/>.</summary>
        public static bool IsNewer(string? candidate, string? installed)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return false;
            if (string.IsNullOrWhiteSpace(installed))
                return true;

            if (TryParse(candidate, out var cv) && TryParse(installed, out var iv))
            {
                var result = cv.CompareTo(iv);
                if (result != 0)
                    return result > 0;
                return false;
            }

            return string.Compare(candidate, installed, StringComparison.OrdinalIgnoreCase) > 0;
        }

        /// <summary>
        /// A build change is an update when the version is equal and both build ids are
        /// known. A remote build older than the installed one is never offered.
        /// </summary>
        public static bool IsBuildUpdate(string? candidateVersion, string? candidateBuild,
            string? installedVersion, string? installedBuild)
        {
            if (IsNewer(candidateVersion, installedVersion))
                return true;
            if (string.IsNullOrWhiteSpace(candidateVersion) || string.IsNullOrWhiteSpace(installedVersion))
                return true;
            if (TryParse(candidateVersion, out var cv) && TryParse(installedVersion, out var iv) && cv < iv)
                return false;

            return !string.IsNullOrWhiteSpace(candidateBuild)
                && !string.IsNullOrWhiteSpace(installedBuild)
                && !string.Equals(candidateBuild.Trim(), installedBuild.Trim(), StringComparison.OrdinalIgnoreCase)
                && CompareBuildIds(candidateBuild, installedBuild) > 0;
        }

        private static int CompareBuildIds(string left, string right)
        {
            if (long.TryParse(left, out var l) && long.TryParse(right, out var r))
                return l.CompareTo(r);
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryParse(string? value, out Version version)
        {
            version = new Version(0, 0);
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var text = value.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                text = text[1..];

            // Strip a channel/build suffix: 1.2.3-beta.4 / 1.2.3+build
            var cut = text.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0)
                text = text[..cut];

            if (!Version.TryParse(text, out var parsed))
                return false;

            version = new Version(
                parsed.Major,
                parsed.Minor,
                Math.Max(0, parsed.Build),
                Math.Max(0, parsed.Revision));
            return true;
        }

        /// <summary>True when two version strings describe the same normalized version.</summary>
        public static bool AreSameVersion(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;
            return TryParse(left, out var l) && TryParse(right, out var r) && l == r;
        }
    }
}
