namespace Vellum.Migrator;

public sealed class MigrationOptions
{
    public bool Silent { get; private set; }
    public bool DryRun { get; private set; }
    public bool MigrateProfile { get; set; } = true;
    public bool UninstallOld { get; set; }
    public bool LaunchVellum { get; set; } = true;
    public bool ShowHelp { get; private set; }

    public string RoamingRoot { get; private set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string InstallRoot { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");

    public string LogPath { get; private set; } =
        Path.Combine(Path.GetTempPath(), "Vellum-Migrator.log");

    public string? VellumInstallerPath { get; set; }

    public static MigrationOptions Parse(string[] args)
    {
        var options = new MigrationOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--silent":
                    options.Silent = true;
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--no-profile":
                    options.MigrateProfile = false;
                    break;
                case "--uninstall-old":
                    options.UninstallOld = true;
                    break;
                case "--no-uninstall":
                    options.UninstallOld = false;
                    break;
                case "--no-launch":
                    options.LaunchVellum = false;
                    break;
                case "--launch":
                    options.LaunchVellum = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    options.ShowHelp = true;
                    break;
                case "--roaming":
                    if (i + 1 < args.Length)
                        options.RoamingRoot = Path.GetFullPath(args[++i]);
                    break;
                case "--install-root":
                    if (i + 1 < args.Length)
                        options.InstallRoot = Path.GetFullPath(args[++i]);
                    break;
                case "--log":
                    if (i + 1 < args.Length)
                        options.LogPath = Path.GetFullPath(args[++i]);
                    break;
            }
        }

        return options;
    }
}
