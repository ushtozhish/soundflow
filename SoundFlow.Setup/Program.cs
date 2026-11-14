using System.Diagnostics;
using System.Reflection;
using System.Globalization;
using Microsoft.Win32;

namespace SoundFlowSetup;

internal static class Program
{
    static readonly bool En = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is not ("ru" or "kk" or "be" or "uk" or "ky" or "uz");
    internal static string L(string ru, string en) => En ? en : ru;

    internal const string Version = "1.3.0";
    internal static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SoundFlow");
    internal static readonly string AppPath = Path.Combine(InstallDir, "SoundFlow.exe");
    internal static readonly string ApoDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO");
    internal static bool ApoInstalled => File.Exists(Path.Combine(ApoDir, "EqualizerAPO.dll")) && Directory.Exists(Path.Combine(ApoDir, "config"));

    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Old 1.0 uninstall entries point to "Uninstall.exe /uninstall" (a copy of this installer) — forward to the app.
        if (args.Contains("/uninstall", StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(AppPath)) Process.Start(new ProcessStartInfo(AppPath, "--uninstall") { UseShellExecute = true });
            return;
        }
        Application.Run(new InstallForm());
    }

    internal static Icon AppIcon() => Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

    internal static Image Logo()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoundFlow.logo.png") ?? throw new InvalidOperationException(L("Не найдена иконка", "Icon not found"));
        return new Bitmap(stream);
    }

    internal static void Extract(string resource, string destination)
    {
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource) ?? throw new InvalidOperationException(L("Не найден встроенный файл: ", "Embedded file not found: ") + resource);
        using var output = File.Create(destination);
        input.CopyTo(output);
    }

    internal static void Shortcut(string path, string target, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException(L("Не удалось создать ярлык", "Could not create a shortcut"));
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        link.IconLocation = target + ",0";
        link.Description = description;
        link.Save();
    }

    internal static void StopRunningApp()
    {
        foreach (var p in Process.GetProcessesByName("SoundFlow"))
        {
            try { p.Kill(true); p.WaitForExit(5000); } catch { }
        }
    }

    internal static int RunAndWait(string file, string args, bool shell)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = shell, CreateNoWindow = !shell });
        if (p is null) return -1;
        p.WaitForExit();
        return p.ExitCode;
    }
}

internal sealed class InstallForm : Form
{
    static string L(string ru, string en) => Program.L(ru, en);
    bool finished;
    readonly Button install = new() { Text = L("Установить", "Install"), Height = 48, Dock = DockStyle.Bottom };
    readonly ProgressBar progress = new() { Dock = DockStyle.Bottom, Height = 7, Style = ProgressBarStyle.Marquee, Visible = false };
    readonly CheckBox desktop = new() { Text = L("Создать ярлык на рабочем столе", "Create a desktop shortcut"), Checked = true, AutoSize = true };
    readonly Label state = new() { AutoSize = false, Height = 46, Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleLeft };

    public InstallForm()
    {
        Text = L("Установка SoundFlow ", "SoundFlow setup ") + Program.Version;
        Icon = Program.AppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoScaleMode = AutoScaleMode.Dpi;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new(600, 440);
        BackColor = Color.FromArgb(10, 11, 15);
        ForeColor = Color.White;
        Font = new("Segoe UI", 10);
        Padding = new(34);

        var logo = new PictureBox { Image = Program.Logo(), SizeMode = PictureBoxSizeMode.Zoom, Size = new(88, 88), Location = new(34, 32) };
        var title = new Label { Text = "SOUNDFLOW", Font = new("Segoe UI", 28, FontStyle.Bold), ForeColor = Color.FromArgb(200, 255, 56), AutoSize = true, Location = new(140, 42) };
        var sub = new Label { Text = L("Системный эквалайзер Windows · версия ", "System equalizer for Windows · version ") + Program.Version, ForeColor = Color.FromArgb(160, 165, 175), AutoSize = true, Location = new(144, 92) };
        string apoText = Program.ApoInstalled
            ? L("Equalizer APO уже установлен — он не будет переустановлен, ваши устройства останутся как есть.", "Equalizer APO is already installed — it will not be reinstalled, your devices stay as they are.")
            : L("Также будет установлен Equalizer APO 1.4.2 (бесплатный, GPL). В его окне отметьте устройство воспроизведения, на котором нужен эквалайзер. Для завершения потребуется перезагрузка.", "Equalizer APO 1.4.2 (free, GPL) will also be installed. In its window tick the playback device that needs the equalizer. A restart is required to finish.");
        var text = new Label
        {
            Text = L("Установщик добавит SoundFlow в Program Files и в автозапуск (тихо, в область уведомлений). Если SoundFlow уже установлен, он будет обновлён, настройки сохранятся.\n\n",
                     "Setup puts SoundFlow into Program Files and into startup (quietly, in the notification area). If SoundFlow is already installed it is updated and your settings are kept.\n\n") + apoText,
            Location = new(36, 150), Size = new(530, 140), ForeColor = Color.FromArgb(210, 212, 218),
        };
        desktop.Location = new(36, 300);

        install.BackColor = Color.FromArgb(200, 255, 56);
        install.ForeColor = Color.FromArgb(10, 11, 15);
        install.FlatStyle = FlatStyle.Flat;
        install.FlatAppearance.BorderSize = 0;
        install.Font = new("Segoe UI", 11, FontStyle.Bold);
        install.Click += async (_, _) =>
        {
            if (finished)
            {
                Process.Start(new ProcessStartInfo(Program.AppPath) { UseShellExecute = true });
                Close();
            }
            else await InstallAsync();
        };
        state.ForeColor = Color.FromArgb(160, 165, 175);
        Controls.AddRange([logo, title, sub, text, desktop, state, progress, install]);
    }

    async Task InstallAsync()
    {
        install.Enabled = false;
        desktop.Enabled = false;
        progress.Visible = true;
        bool needReboot = false;
        try
        {
            state.Text = L("Копирование SoundFlow…", "Copying SoundFlow…");
            bool createDesktop = desktop.Checked;
            await Task.Run(() =>
            {
                Program.StopRunningApp();
                Directory.CreateDirectory(Program.InstallDir);
                Program.Extract("SoundFlow.exe", Program.AppPath);
                // leftovers of version 1.0
                foreach (var old in new[] { "Uninstall.exe", "EqualizerAPO-x64-1.4.2.exe" })
                    try { File.Delete(Path.Combine(Program.InstallDir, old)); } catch { }

                string start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "SoundFlow.lnk");
                Program.Shortcut(start, Program.AppPath, L("Системный эквалайзер SoundFlow", "SoundFlow system equalizer"));
                if (createDesktop)
                    Program.Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "SoundFlow.lnk"), Program.AppPath, L("Системный эквалайзер SoundFlow", "SoundFlow system equalizer"));

                using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SoundFlow");
                key.SetValue("DisplayName", "SoundFlow");
                key.SetValue("DisplayVersion", Program.Version);
                key.SetValue("Publisher", "SoundFlow");
                key.SetValue("DisplayIcon", Program.AppPath);
                key.SetValue("InstallLocation", Program.InstallDir);
                key.SetValue("UninstallString", $"\"{Program.AppPath}\" --uninstall");
                key.SetValue("QuietUninstallString", $"\"{Program.AppPath}\" --uninstall --quiet");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(new FileInfo(Program.AppPath).Length / 1024), RegistryValueKind.DWord);
            });

            if (!Program.ApoInstalled)
            {
                state.Text = L("Установка Equalizer APO… Отметьте в его окне своё устройство воспроизведения.", "Installing Equalizer APO… Tick your playback device in its window.");
                string apo = Path.Combine(Path.GetTempPath(), "EqualizerAPO-x64-1.4.2.exe");
                await Task.Run(() => Program.Extract("EqualizerAPO.exe", apo));
                await Task.Run(() => Program.RunAndWait(apo, "", shell: true));
                try { File.Delete(apo); } catch { }
                needReboot = true;
                if (!Program.ApoInstalled) throw new InvalidOperationException(L("Equalizer APO не установлен — без него SoundFlow не сможет менять звук. Запустите установку ещё раз.", "Equalizer APO is not installed — SoundFlow cannot change the sound without it. Run the setup again."));
            }

            state.Text = L("Подключение SoundFlow к Equalizer APO…", "Connecting SoundFlow to Equalizer APO…");
            int code = await Task.Run(() => Program.RunAndWait(Program.AppPath, "--prepare-apo", shell: false));
            if (code != 0) throw new InvalidOperationException(L("Не удалось настроить конфигурацию Equalizer APO (подробности в журнале SoundFlow).", "Could not configure Equalizer APO (see the SoundFlow log)."));

            state.Text = needReboot
                ? L("Готово. Перезагрузите Windows, чтобы Equalizer APO начал работать.", "Done. Restart Windows so Equalizer APO starts working.")
                : L("Готово. Настройки сохранены, эквалайзер работает.", "Done. Settings kept, the equalizer is working.");
            progress.Visible = false;
            install.Text = L("Запустить SoundFlow", "Launch SoundFlow");
            finished = true;
            install.Enabled = true;
        }
        catch (Exception ex)
        {
            progress.Visible = false;
            state.Text = L("Ошибка: ", "Error: ") + ex.Message;
            state.ForeColor = Color.FromArgb(255, 98, 90);
            install.Enabled = true;
            desktop.Enabled = true;
        }
    }
}
