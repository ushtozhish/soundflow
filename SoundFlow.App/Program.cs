using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using static SoundFlow.Lang;

namespace SoundFlow;

internal static class Program
{
    const string MutexName = "Local\\SoundFlow.NativeEqualizer";
    const string ShowEventName = "Local\\SoundFlow.Show";

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Lang.Set(Store.PeekLanguage());
        Application.ThreadException += (_, e) =>
        {
            Log.Error("Необработанная ошибка в интерфейсе", e.Exception);
            MessageBox.Show(L("Произошла ошибка, SoundFlow продолжает работать.\n\n", "An error occurred, SoundFlow keeps running.\n\n", "Қате орын алды, SoundFlow жұмысын жалғастыруда.\n\n") + e.Exception.Message +
                L("\n\nПодробности — в журнале (меню в трее → «Папка с настройками и журналом»).", "\n\nDetails are in the log (tray menu → “Settings and log folder”).", "\n\nТолығы — журналда (науадағы мәзір → «Баптаулар мен журнал қалтасы»)."),
                "SoundFlow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Критическая ошибка", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Фоновая ошибка", e.Exception); e.SetObserved(); };

        if (Has(args, "--prepare-apo")) return PrepareApo();
        int shots = Array.FindIndex(args, a => a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase));
        if (shots >= 0 && shots + 1 < args.Length) return Screenshots(args[shots + 1]);
        if (Has(args, "--uninstall")) return Uninstaller.Run(silent: Has(args, "--quiet"));

        // after a language switch the new copy waits for the old one to exit
        int waitIndex = Array.FindIndex(args, a => a.Equals("--wait", StringComparison.OrdinalIgnoreCase));
        if (waitIndex >= 0 && waitIndex + 1 < args.Length && int.TryParse(args[waitIndex + 1], out int oldPid))
            try { using var old = Process.GetProcessById(oldPid); old.WaitForExit(10000); } catch { }

        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running: ask the running copy to show its window.
            try { using var show = EventWaitHandle.OpenExisting(ShowEventName); show.Set(); } catch { }
            return 0;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        Log.Info($"Запуск SoundFlow {Application.ProductVersion}");
        var form = new MainForm(startHidden: Has(args, "--tray"));
        var registration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
        {
            try { form.BeginInvoke(new Action(form.RestoreWindow)); } catch { }
        }, null, -1, false);
        Application.Run(form);
        registration.Unregister(null);
        return 0;
    }

    /// <summary>Demo screenshots for the README in every language (used by GitHub Actions).</summary>
    static int Screenshots(string folder)
    {
        foreach (var code in new[] { "en", "ru", "kk" })
        {
            Lang.Set(code);
            using var form = new MainForm(startHidden: false, demo: true)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(1240, 800),
            };
            form.Show();
            form.CaptureScreens(folder);
            form.Hide();
        }
        return 0;
    }

    static bool Has(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    public static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Elevated helper: makes soundflow.txt writable for the normal user and hooks it into APO.</summary>
    static int PrepareApo()
    {
        try
        {
            Apo.PrepareConfig();
            Log.Info("Конфиг APO подготовлен (права и Include)");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Подготовка конфига APO", ex);
            MessageBox.Show(L("Не удалось настроить Equalizer APO:\n", "Could not set up Equalizer APO:\n", "Equalizer APO баптау мүмкін болмады:\n") + ex.Message, "SoundFlow", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

/// <summary>Uninstall logic lives in SoundFlow.exe itself, so no second copy of the installer is kept on disk.</summary>
internal static class Uninstaller
{
    static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SoundFlow");

    public static int Run(bool silent)
    {
        if (!Program.IsAdmin())
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--uninstall" + (silent ? " --quiet" : "")) { UseShellExecute = true, Verb = "runas" });
            }
            catch (System.ComponentModel.Win32Exception) { }
            return 0;
        }

        bool removeSettings = false;
        if (!silent)
        {
            var answer = MessageBox.Show(L(
                "Удалить SoundFlow с этого компьютера?\n\nНастройки SoundFlow будут убраны из Equalizer APO, звук станет как без эквалайзера.\n" +
                "Сам Equalizer APO останется (его можно удалить отдельно в «Установке и удалении программ»).\n\n" +
                "Да — удалить и стереть мои профили и настройки\nНет — удалить, но оставить настройки\nОтмена — ничего не делать",
                "Remove SoundFlow from this computer?\n\nSoundFlow settings will be removed from Equalizer APO, sound returns to no EQ.\n" +
                "Equalizer APO itself stays (remove it separately in “Apps & features”).\n\n" +
                "Yes — remove and erase my profiles and settings\nNo — remove but keep settings\nCancel — do nothing",
                "SoundFlow-ды осы компьютерден жою керек пе?\n\nSoundFlow баптаулары Equalizer APO-дан алынады, дыбыс эквалайзерсіз күйге оралады.\n" +
                "Equalizer APO өзі қалады (оны «Бағдарламаларды орнату және жою» арқылы бөлек жоюға болады).\n\n" +
                "Иә — жою және профильдер мен баптауларды өшіру\nЖоқ — жою, бірақ баптауларды қалдыру\nБас тарту — ештеңе істемеу"),
                L("Удаление SoundFlow", "Uninstall SoundFlow", "SoundFlow жою"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return 0;
            removeSettings = answer == DialogResult.Yes;
        }

        try
        {
            int self = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("SoundFlow"))
            {
                if (p.Id == self) continue;
                try { p.Kill(true); p.WaitForExit(5000); } catch { }
            }
            try { Apo.RemoveConfig(); } catch (Exception ex) { Log.Error("Удаление конфига APO", ex); }

            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "SoundFlow.lnk"));
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "SoundFlow.lnk"));
            try { using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); run?.DeleteValue("SoundFlow", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\SoundFlow", false); } catch { }
            Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SoundFlow", false);
            if (removeSettings) try { Directory.Delete(Store.Folder, true); } catch { }

            // The running exe cannot delete itself — let cmd remove the folder after we exit.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"") { CreateNoWindow = true, UseShellExecute = false });
            if (!silent) MessageBox.Show(L("SoundFlow удалён.", "SoundFlow has been removed.", "SoundFlow жойылды."), "SoundFlow", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Удаление", ex);
            if (!silent) MessageBox.Show(ex.Message, L("Не удалось удалить SoundFlow", "Could not remove SoundFlow", "SoundFlow жою мүмкін болмады"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
