using System.Windows.Forms;

namespace Vellum.Migrator;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = MigrationOptions.Parse(args);

        if (options.ShowHelp)
        {
            ShowHelp();
            return 0;
        }

        if (options.Silent)
        {
            var service = new MigrationService(options);
            var result = service.Run(Console.WriteLine);
            return result.Success ? 0 : 1;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MigrationForm(options));
        return 0;
    }

    private static void ShowHelp()
    {
        const string help = """
            Vellum Migrator - переход с Nexora на Vellum.

            Использование:
              Vellum-Migrator.exe                 обычный запуск с окном
              Vellum-Migrator.exe --silent        без окна, результат в лог
              Vellum-Migrator.exe --dry-run       показать план и ничего не менять

            Параметры:
              --no-profile        не переносить профиль (%APPDATA%\Nexora -> %APPDATA%\Vellum)
              --uninstall-old     удалить старую версию Nexora
              --no-launch         не запускать Vellum после переноса
              --installer <path>  явно указать Vellum-Setup-*.exe
              --install-dir <path>  куда установить Vellum (передаётся setup)
              --roaming <path>    другой каталог профилей (для проверки)
              --install-root <path>  другой каталог установки (для проверки)
              --log <path>        файл журнала
              --help              эта справка

            Журнал по умолчанию: %TEMP%\Vellum-Migrator.log
            """;

        var log = Path.Combine(Path.GetTempPath(), "Vellum-Migrator.log");
        try
        {
            File.WriteAllText(log, help);
        }
        catch
        {
            // A read-only temp directory must not break the help output.
        }

        MessageBox.Show(help, "Vellum Migrator", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
