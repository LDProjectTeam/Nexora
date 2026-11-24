using System.Windows.Forms;

namespace Vellum.Migrator;

public sealed class MigrationForm : Form
{
    private readonly MigrationOptions _options;
    private readonly Label _info = new();
    private readonly Label _status = new();
    private readonly CheckBox _profile = new();
    private readonly CheckBox _uninstall = new();
    private readonly CheckBox _launch = new();
    private readonly CheckBox _replace = new();
    private readonly TextBox _installDir = new();
    private readonly Button _browse = new();
    private readonly Button _choose = new();
    private readonly TextBox _log = new();
    private readonly Button _migrate = new();
    private readonly Button _close = new();
    private readonly string _defaultInstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Vellum");

    public MigrationForm(MigrationOptions options)
    {
        _options = options;
        BuildLayout();
        LoadDetection();
    }

    private void BuildLayout()
    {
        Text = "Vellum — переход с Nexora";
        ClientSize = new Size(660, 555);
        MinimumSize = new Size(660, 555);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9f);
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);

        var title = new Label
        {
            Text = "Переход на Vellum",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 16)
        };

        var subtitle = new Label
        {
            Text = "Перенесёт профиль Nexora в Vellum, при желании удалит старую версию и установит Vellum.",
            AutoSize = true,
            Location = new Point(22, 52),
            ForeColor = SystemColors.GrayText
        };

        _info.Location = new Point(22, 84);
        _info.Size = new Size(616, 64);
        _info.AutoEllipsis = true;

        _choose.Text = "Выбрать Vellum-Setup…";
        _choose.Location = new Point(452, 148);
        _choose.Size = new Size(186, 26);
        _choose.Click += (_, _) => ChooseInstaller();

        var installLabel = new Label
        {
            Text = "Куда установить Vellum:",
            AutoSize = true,
            Location = new Point(22, 188)
        };

        _installDir.Location = new Point(196, 184);
        _installDir.Size = new Size(300, 24);
        _installDir.Text = _options.InstallDirectory ?? _defaultInstallDir;

        _browse.Text = "Обзор…";
        _browse.Location = new Point(504, 182);
        _browse.Size = new Size(134, 26);
        _browse.Click += (_, _) => ChooseInstallDirectory();

        _replace.Text = "Установить в папку Nexora (заменить старую версию)";
        _replace.Location = new Point(22, 218);
        _replace.AutoSize = true;
        _replace.CheckedChanged += (_, _) => OnReplaceChanged();

        _profile.Text = "Перенести профиль и настройки";
        _profile.Checked = true;
        _profile.Location = new Point(22, 248);
        _profile.AutoSize = true;

        _uninstall.Text = "Удалить старую версию Nexora (может потребоваться UAC)";
        _uninstall.Location = new Point(22, 274);
        _uninstall.AutoSize = true;

        _launch.Text = "Запустить Vellum после переноса";
        _launch.Checked = true;
        _launch.Location = new Point(22, 300);
        _launch.AutoSize = true;

        var logLabel = new Label
        {
            Text = "Журнал:",
            AutoSize = true,
            Location = new Point(22, 332)
        };

        _log.Location = new Point(22, 352);
        _log.Size = new Size(616, 140);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = SystemColors.Window;
        _log.Font = new Font("Consolas", 8.5f);

        _status.Location = new Point(22, 506);
        _status.Size = new Size(400, 20);
        _status.ForeColor = SystemColors.GrayText;

        _migrate.Text = "Перейти на Vellum";
        _migrate.Location = new Point(432, 500);
        _migrate.Size = new Size(124, 30);
        _migrate.Click += OnMigrate;

        _close.Text = "Закрыть";
        _close.Location = new Point(564, 500);
        _close.Size = new Size(74, 30);
        _close.Click += (_, _) => Close();

        AcceptButton = _migrate;
        CancelButton = _close;

        Controls.AddRange(new Control[]
        {
            title, subtitle, _info, _choose, installLabel, _installDir, _browse, _replace,
            _profile, _uninstall, _launch, logLabel, _log, _status, _migrate, _close
        });
    }

    private void LoadDetection()
    {
        var service = new MigrationService(_options);
        var legacy = service.LegacyUninstaller
            ?? (service.LegacyExecutable is null
                ? "не установлена"
                : service.LegacyExecutable + " (портативная)");

        _info.Text =
            $"Профиль Nexora: {service.LegacyProfile ?? "не найден"}{Environment.NewLine}" +
            $"Профиль Vellum: {service.TargetProfile}{Environment.NewLine}" +
            $"Старая версия: {legacy}{Environment.NewLine}" +
            $"Новая версия: {service.VellumExecutable ?? service.VellumInstaller ?? "не найдена"}";

        _profile.Enabled = service.LegacyProfile is not null;
        _profile.Checked = _profile.Enabled;
        _uninstall.Enabled = service.LegacyUninstaller is not null;
        _launch.Enabled = service.VellumExecutable is not null || service.VellumInstaller is not null;
        _launch.Checked = _launch.Enabled;

        _replace.Enabled = service.LegacyUninstaller is not null && service.LegacyExecutable is not null;
        if (!_replace.Enabled)
            _replace.Checked = false;

        if (service.LegacyProfile is null && Directory.Exists(service.TargetProfile))
            _status.Text = "Профиль Nexora не найден — возможно, переход уже выполнен.";
    }

    private void OnReplaceChanged()
    {
        if (_replace.Checked)
        {
            var service = new MigrationService(_options);
            var directory = Path.GetDirectoryName(service.LegacyExecutable);
            if (!string.IsNullOrWhiteSpace(directory))
                _installDir.Text = directory;

            if (_uninstall.Enabled)
                _uninstall.Checked = true;

            _status.Text = "Vellum будет установлен в папку Nexora; игры в Library останутся.";
        }
        else
        {
            _installDir.Text = _defaultInstallDir;
            _status.Text = string.Empty;
        }
    }

    private bool ChooseInstaller()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Выберите установщик Vellum",
            Filter = "Установщик Vellum (Vellum-Setup-*.exe)|Vellum-Setup-*.exe|Программы (*.exe)|*.exe",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return false;

        _options.VellumInstallerPath = dialog.FileName;
        LoadDetection();
        return true;
    }

    private void ChooseInstallDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Куда установить Vellum",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_installDir.Text) ? _installDir.Text : _defaultInstallDir
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _installDir.Text = dialog.SelectedPath;
    }

    private async void OnMigrate(object? sender, EventArgs e)
    {
        if (_launch.Checked)
        {
            var current = new MigrationService(_options);
            if (current.VellumExecutable is null && current.VellumInstaller is null && !ChooseInstaller())
            {
                _status.Text = "Vellum не найден: выбери установщик.";
                return;
            }
        }

        _migrate.Enabled = false;
        _close.Enabled = false;
        _status.Text = "Перенос…";

        _options.MigrateProfile = _profile.Checked;
        _options.UninstallOld = _uninstall.Checked;
        _options.LaunchVellum = _launch.Checked;
        _options.InstallDirectory = string.IsNullOrWhiteSpace(_installDir.Text)
            ? null
            : _installDir.Text.Trim();

        var result = await Task.Run(() => new MigrationService(_options).Run());

        _log.Text = string.Join(Environment.NewLine, result.Messages);
        _status.Text = result.Success ? "Готово." : "Завершено с ошибками — смотри журнал.";
        _migrate.Enabled = true;
        _close.Enabled = true;
    }
}
