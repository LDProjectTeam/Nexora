using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Nexora.App.Services
{
    internal sealed record WindowsShortcutInfo(string TargetPath, string Arguments, string WorkingDirectory);

    internal static class WindowsShortcutReader
    {
        public static WindowsShortcutInfo Read(string shortcutPath)
        {
            if (!File.Exists(shortcutPath) || !string.Equals(Path.GetExtension(shortcutPath), ".lnk", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Select a Windows shortcut (.lnk) file.");

            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
            object? shell = null;
            object? shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("Could not read the Windows shortcut.");
                dynamic shellAutomation = shell;
                shortcut = shellAutomation.CreateShortcut(Path.GetFullPath(shortcutPath));
                dynamic link = shortcut;

                string target = Convert.ToString(link.TargetPath) ?? string.Empty;
                string arguments = Convert.ToString(link.Arguments) ?? string.Empty;
                string workingDirectory = Convert.ToString(link.WorkingDirectory) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(target))
                    throw new InvalidDataException("The shortcut has no target.");

                target = Environment.ExpandEnvironmentVariables(target.Trim());
                if (!Path.IsPathRooted(target) || !File.Exists(target))
                    throw new FileNotFoundException("The shortcut target does not exist.", target);

                if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
                    workingDirectory = Path.GetDirectoryName(target) ?? string.Empty;

                return new WindowsShortcutInfo(target, arguments, workingDirectory);
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut))
                    Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell))
                    Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
