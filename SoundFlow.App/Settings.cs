using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SoundFlow;

/// <summary>One parametric EQ band.</summary>
internal sealed class Band
{
    public bool Enabled { get; set; } = true;
    /// <summary>PK, LS, HS, LP, HP, NO (notch), BP (band-pass).</summary>
    public string Type { get; set; } = "PK";
    public double Frequency { get; set; } = 1000;
    public double Gain { get; set; }
    public double Q { get; set; } = 1;
    /// <summary>Only for LP / HP: 12, 24, 36 or 48 dB/oct.</summary>
    public int Slope { get; set; } = 12;
    /// <summary>Empty = all channels, otherwise 1-based channel numbers separated by spaces.</summary>
    public string Channels { get; set; } = "";

    public const double MinGain = -60, MaxGain = 24;
    public Band Clone() => (Band)MemberwiseClone();
    public static bool HasGain(string type) => type is "PK" or "LS" or "HS";
}

internal sealed class ChannelTrim
{
    public double Gain { get; set; }
    public bool Mute { get; set; }
    public bool Solo { get; set; }
}

internal sealed class EqProfile
{
    public string Name { get; set; } = "";
    public int Mode { get; set; }
    public double[] Graphic { get; set; } = new double[10];
    public List<Band> Parametric { get; set; } = [];
}

internal sealed class Settings
{
    public int Version { get; set; } = 2;
    public bool Enabled { get; set; } = true;
    /// <summary>User preamp in dB. SoundFlow never changes it by itself.</summary>
    public double Preamp { get; set; }
    /// <summary>When on, the EQ boost is subtracted from the output gain (the slider is not touched).</summary>
    public bool AutoCompensate { get; set; }
    public int Mode { get; set; }
    public double[] Graphic { get; set; } = new double[10];
    public List<Band> Parametric { get; set; } = DefaultParametric();
    public string Profile { get; set; } = "flat";
    public List<EqProfile> UserProfiles { get; set; } = [];
    public Dictionary<int, ChannelTrim> Channels { get; set; } = [];
    public bool StartWithWindows { get; set; } = true;
    public int DisplayRange { get; set; } = 12;
    /// <summary>"" = by Windows language, "ru" or "en".</summary>
    public string Language { get; set; } = "";

    public static List<Band> DefaultParametric() =>
    [
        new() { Type = "HP", Frequency = 30, Q = .71 },
        new() { Type = "PK", Frequency = 120, Gain = 2, Q = 1 },
        new() { Type = "PK", Frequency = 500, Q = 1 },
        new() { Type = "PK", Frequency = 2500, Q = 1 },
        new() { Type = "PK", Frequency = 8000, Gain = 1, Q = 1 },
        new() { Type = "LP", Frequency = 19000, Q = .71 },
    ];

    public void Normalize()
    {
        Preamp = Math.Clamp(Preamp, -12, 12);
        if (Graphic is null || Graphic.Length != 10) Graphic = new double[10];
        for (int i = 0; i < 10; i++) Graphic[i] = Math.Clamp(Graphic[i], -12, 12);
        Parametric ??= DefaultParametric();
        if (Parametric.Count == 0) Parametric = DefaultParametric();
        if (Parametric.Count > 24) Parametric = Parametric.Take(24).ToList();
        foreach (var b in Parametric) Store.NormalizeBand(b);
        Mode = Math.Clamp(Mode, 0, 1);
        DisplayRange = DisplayRange is 12 or 24 or 48 ? DisplayRange : 12;
        Language = Language is "ru" or "en" ? Language : "";
        UserProfiles ??= [];
        Channels ??= [];
        Profile ??= "";
    }
}

[JsonSerializable(typeof(Settings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class SettingsJson : JsonSerializerContext;

/// <summary>Settings persistence: %AppData%\SoundFlow\settings.json, atomic write + backup.</summary>
internal static class Store
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundFlow");
    static string FilePath => Path.Combine(Folder, "settings.json");
    static string BackupPath => Path.Combine(Folder, "settings.bak");
    static readonly object Gate = new();

    /// <summary>Returns settings and whether they were really found (false = fresh defaults).</summary>
    public static (Settings settings, bool found) Load()
    {
        foreach (var path in new[] { FilePath, BackupPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var s = JsonSerializer.Deserialize(File.ReadAllText(path, Encoding.UTF8), SettingsJson.Default.Settings);
                if (s is null) continue;
                s.Normalize();
                Log.Info($"Настройки загружены из {Path.GetFileName(path)}, предусиление {s.Preamp:0.0} dB");
                return (s, true);
            }
            catch (Exception ex) { Log.Error($"Не удалось прочитать {path}", ex); }
        }
        var migrated = MigrateFromRegistry();
        if (migrated is not null) { Save(migrated); return (migrated, true); }
        var fresh = new Settings();
        fresh.Normalize();
        return (fresh, false);
    }

    public static void Save(Settings s)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string json = JsonSerializer.Serialize(s, SettingsJson.Default.Settings);
                string tmp = FilePath + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(json);
                    fs.Write(bytes);
                    fs.Flush(true);
                }
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, BackupPath, true);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log.Error("Не удалось сохранить настройки", ex); }
        }
    }

    /// <summary>Reads settings of SoundFlow 1.0 (registry) once.</summary>
    static Settings? MigrateFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\SoundFlow");
            var a = key?.GetValue("Curve")?.ToString()?.Split(';');
            if (a is null || a.Length != 13) return null;
            var s = new Settings
            {
                Enabled = a[0] == "1",
                Preamp = int.Parse(a[1]) / 2d,
                Mode = Convert.ToInt32(key!.GetValue("Mode") ?? 0),
            };
            for (int i = 0; i < 10; i++) s.Graphic[i] = int.Parse(a[i + 3]) / 2d;
            var json = key.GetValue("Parametric")?.ToString();
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                var list = new List<Band>();
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    list.Add(new Band
                    {
                        Enabled = e.TryGetProperty("Enabled", out var en) && en.GetBoolean(),
                        Type = e.TryGetProperty("Type", out var t) ? t.GetString() ?? "PK" : "PK",
                        Frequency = e.TryGetProperty("Frequency", out var f) ? f.GetDouble() : 1000,
                        Gain = e.TryGetProperty("Gain", out var g) ? g.GetDouble() : 0,
                        Q = e.TryGetProperty("Q", out var q) ? q.GetDouble() : 1,
                    });
                }
                if (list.Count > 0) s.Parametric = list;
            }
            s.Normalize();
            Log.Info($"Перенесены настройки SoundFlow 1.0 из реестра, предусиление {s.Preamp:0.0} dB");
            return s;
        }
        catch (Exception ex) { Log.Error("Перенос настроек из реестра не удался", ex); return null; }
    }

    /// <summary>Reads only the language setting (used before the main window exists).</summary>
    public static string PeekLanguage()
    {
        try
        {
            if (!File.Exists(FilePath)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
            return doc.RootElement.TryGetProperty("Language", out var l) ? l.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    public static void NormalizeBand(Band b)
    {
        b.Type = b.Type is "PK" or "LS" or "HS" or "LP" or "HP" or "NO" or "BP" ? b.Type : "PK";
        b.Frequency = Math.Clamp(double.IsFinite(b.Frequency) ? b.Frequency : 1000, 10, 22000);
        b.Gain = Math.Clamp(double.IsFinite(b.Gain) ? b.Gain : 0, Band.MinGain, Band.MaxGain);
        b.Q = Math.Clamp(double.IsFinite(b.Q) ? b.Q : 1, .1, 30);
        b.Slope = b.Slope is 12 or 24 or 36 or 48 ? b.Slope : 12;
        b.Channels ??= "";
    }
}

internal static class Log
{
    static readonly object Gate = new();
    static string PathOf => System.IO.Path.Combine(Store.Folder, "soundflow.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Store.Folder);
                var file = new FileInfo(PathOf);
                if (file.Exists && file.Length > 1_000_000)
                {
                    File.Copy(file.FullName, file.FullName + ".old", true);
                    file.Delete();
                }
                File.AppendAllText(PathOf, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { /* logging must never crash the app */ }
        }
    }
}

/// <summary>Two-language UI: Russian and English.</summary>
internal static class Lang
{
    public static bool En { get; private set; }

    public static void Set(string? code)
    {
        string system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        En = code switch
        {
            "en" => true,
            "ru" => false,
            _ => system is not ("ru" or "kk" or "be" or "uk" or "ky" or "uz"),
        };
    }

    public static string L(string ru, string en) => En ? en : ru;
}
