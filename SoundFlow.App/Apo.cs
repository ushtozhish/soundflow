using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.Win32;

namespace SoundFlow;

internal enum ApoWriteResult { Ok, NotInstalled, NoAccess, Error }

/// <summary>Everything that touches Equalizer APO.</summary>
internal static class Apo
{
    public const string IncludeLine = "Include: soundflow.txt";
    static readonly string[] Roots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "EqualizerAPO"),
    ];

    public static string? Root => Roots.FirstOrDefault(r => Directory.Exists(Path.Combine(r, "config")));
    public static string? ConfigDir => Root is { } r ? Path.Combine(r, "config") : null;
    public static string? TargetFile => ConfigDir is { } c ? Path.Combine(c, "soundflow.txt") : null;
    public static string? Configurator => Root is { } r && File.Exists(Path.Combine(r, "Configurator.exe")) ? Path.Combine(r, "Configurator.exe") : null;

    static string F(double v, string fmt = "0.##") => v.ToString(fmt, CultureInfo.InvariantCulture);

    /// <summary>Builds the text of soundflow.txt.</summary>
    public static string Build(Settings s, double effectivePreamp, int soloBand, int channelCount, string deviceId)
    {
        var sb = new StringBuilder();
        sb.Append("# SoundFlow — автоматически создаётся приложением SoundFlow, правки вручную будут перезаписаны\r\n");
        if (!s.Enabled)
        {
            sb.Append("# Обработка выключена\r\n");
            return sb.ToString();
        }
        sb.Append("Channel: all\r\n");
        sb.Append($"Preamp: {F(effectivePreamp, "0.0")} dB\r\n");

        bool anySolo = s.Channels.Any(c => c.Value.Solo && c.Key >= 1 && c.Key <= channelCount);
        for (int index = 1; index <= Math.Max(channelCount, 1); index++)
        {
            s.Channels.TryGetValue(index, out var trim);
            trim ??= new ChannelTrim();
            bool silent = trim.Mute || (anySolo && !trim.Solo);
            if (!silent && Math.Abs(trim.Gain) < 0.01) continue;
            sb.Append($"Channel: {index}\r\n");
            sb.Append($"Preamp: {(silent ? "-200" : F(trim.Gain, "0.0"))} dB\r\n");
        }
        sb.Append("Channel: all\r\n");

        // routing inside the Windows device itself (channel remap) — done by APO, zero latency
        if (s.Routes.TryGetValue(deviceId, out var self) && self.Custom)
        {
            var parts = new List<string>();
            var pairs = self.Parsed().Where(p => p.Src <= channelCount && p.Dst <= channelCount).ToList();
            for (int dst = 1; dst <= channelCount; dst++)
            {
                var sources = pairs.Where(p => p.Dst == dst).Select(p => p.Src.ToString()).ToList();
                if (sources.Count == 1 && sources[0] == dst.ToString()) continue;
                parts.Add($"{dst}={(sources.Count == 0 ? "0" : string.Join("+", sources))}");
            }
            if (parts.Count > 0) sb.Append($"Copy: {string.Join(" ", parts)}\r\n");
        }
        if (s.MainDelayMs > 0) sb.Append($"Delay: {s.MainDelayMs} ms\r\n");

        int n = 1;
        if (s.Mode == 0)
        {
            for (int i = 0; i < 10; i++)
                sb.Append($"Filter {n++}: ON PK Fc {MainForm.GraphicFrequencies[i]} Hz Gain {F(s.Graphic[i], "0.0")} dB Q 1.41\r\n");
            return sb.ToString();
        }

        string currentChannels = "";
        for (int i = 0; i < s.Parametric.Count; i++)
        {
            var b = s.Parametric[i];
            bool on = b.Enabled && (soloBand < 0 || soloBand == i);
            string channels = b.Channels.Trim();
            if (channels != currentChannels)
            {
                sb.Append($"Channel: {(channels.Length == 0 ? "all" : channels)}\r\n");
                currentChannels = channels;
            }
            foreach (var line in FilterLines(b))
                sb.Append($"Filter {n++}: {(on ? "ON" : "OFF")} {line}\r\n");
        }
        // "Listen to band": keep only the frequency range this band works on, so you hear what it changes
        if (soloBand >= 0 && soloBand < s.Parametric.Count)
        {
            var solo = s.Parametric[soloBand];
            string channels = solo.Channels.Trim();
            if (channels != currentChannels) { sb.Append($"Channel: {(channels.Length == 0 ? "all" : channels)}\r\n"); currentChannels = channels; }
            foreach (var line in ListenLines(solo)) sb.Append($"Filter {n++}: ON {line}\r\n");
        }
        if (currentChannels.Length > 0) sb.Append("Channel: all\r\n");
        return sb.ToString();
    }

    /// <summary>Isolating filter for "listen to band": the region the band affects.</summary>
    public static IEnumerable<string> ListenLines(Band b)
    {
        double f = b.Frequency;
        switch (b.Type)
        {
            case "LS": yield return $"LPQ Fc {F(Math.Min(f * 1.5, 20000))} Hz Q 0.7071"; yield return $"LPQ Fc {F(Math.Min(f * 1.5, 20000))} Hz Q 0.7071"; break;
            case "HS": yield return $"HPQ Fc {F(Math.Max(f / 1.5, 20))} Hz Q 0.7071"; yield return $"HPQ Fc {F(Math.Max(f / 1.5, 20))} Hz Q 0.7071"; break;
            case "HP": yield return $"LPQ Fc {F(Math.Min(f * 2, 20000))} Hz Q 0.7071"; yield return $"LPQ Fc {F(Math.Min(f * 2, 20000))} Hz Q 0.7071"; break;
            case "LP": yield return $"HPQ Fc {F(Math.Max(f / 2, 20))} Hz Q 0.7071"; yield return $"HPQ Fc {F(Math.Max(f / 2, 20))} Hz Q 0.7071"; break;
            default:
                // band-pass twice = steeper skirts, the bell's own width decides how wide the window is
                double q = Math.Clamp(b.Q, 0.5, 8);
                yield return $"BP Fc {F(f)} Hz Q {F(q)}";
                yield return $"BP Fc {F(f)} Hz Q {F(q)}";
                break;
        }
    }

    static IEnumerable<string> FilterLines(Band b)
    {
        string fc = $"Fc {F(b.Frequency)} Hz";
        switch (b.Type)
        {
            case "LS": yield return $"LSC {fc} Gain {F(b.Gain)} dB Q {F(b.Q)}"; break;
            case "HS": yield return $"HSC {fc} Gain {F(b.Gain)} dB Q {F(b.Q)}"; break;
            case "NO": yield return $"NO {fc} Q {F(b.Q)}"; break;
            case "BP": yield return $"BP {fc} Q {F(b.Q)}"; break;
            case "LP":
            case "HP":
                foreach (var q in Filters.SectionQs(b))
                    yield return $"{b.Type}Q {fc} Q {F(q, "0.####")}";
                break;
            default: yield return $"PK {fc} Gain {F(b.Gain)} dB Q {F(b.Q)}"; break;
        }
    }

    /// <summary>Writes soundflow.txt in place (keeps the file's permissions). Skips identical content.</summary>
    public static ApoWriteResult Write(string content)
    {
        var target = TargetFile;
        if (target is null) return ApoWriteResult.NotInstalled;
        try
        {
            if (File.Exists(target) && File.ReadAllText(target) == content) return IncludePresent() ? ApoWriteResult.Ok : ApoWriteResult.NoAccess;
            var bytes = new UTF8Encoding(false).GetBytes(content);
            using (var fs = new FileStream(target, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
            {
                fs.Write(bytes);
                fs.SetLength(bytes.Length);
                fs.Flush(true);
            }
            return IncludePresent() ? ApoWriteResult.Ok : ApoWriteResult.NoAccess;
        }
        catch (UnauthorizedAccessException) { return ApoWriteResult.NoAccess; }
        catch (IOException ex) { Log.Error("Запись soundflow.txt", ex); return ApoWriteResult.Error; }
    }

    public static string? ReadCurrent()
    {
        try { return TargetFile is { } t && File.Exists(t) ? File.ReadAllText(t) : null; } catch { return null; }
    }

    static bool IncludePresent()
    {
        try
        {
            var main = Path.Combine(ConfigDir!, "config.txt");
            return File.Exists(main) && File.ReadAllLines(main).Any(l => l.Trim().Equals(IncludeLine, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>Requires admin. Creates soundflow.txt, lets ordinary users write it, adds the Include line.</summary>
    public static void PrepareConfig()
    {
        var dir = ConfigDir ?? throw new InvalidOperationException("Equalizer APO не установлен");
        var target = Path.Combine(dir, "soundflow.txt");
        if (!File.Exists(target)) File.WriteAllText(target, "# SoundFlow\r\n", new UTF8Encoding(false));
        RunHidden("icacls.exe", $"\"{target}\" /grant *S-1-5-32-545:(M)");
        var main = Path.Combine(dir, "config.txt");
        var text = File.Exists(main) ? File.ReadAllText(main) : "";
        if (!text.Split('\n').Any(l => l.Trim().Equals(IncludeLine, StringComparison.OrdinalIgnoreCase)))
            File.AppendAllText(main, (text.Length > 0 && !text.EndsWith('\n') ? "\r\n" : "") + "\r\n# SoundFlow system equalizer\r\n" + IncludeLine + "\r\n");
    }

    /// <summary>Requires admin. Removes SoundFlow from the APO configuration so nothing keeps processing after uninstall.</summary>
    public static void RemoveConfig()
    {
        var dir = ConfigDir;
        if (dir is null) return;
        var main = Path.Combine(dir, "config.txt");
        if (File.Exists(main))
        {
            var lines = File.ReadAllLines(main).Where(l =>
                !l.Trim().Equals(IncludeLine, StringComparison.OrdinalIgnoreCase) &&
                !l.Trim().Equals("# SoundFlow system equalizer", StringComparison.OrdinalIgnoreCase)).ToList();
            File.WriteAllText(main, string.Join("\r\n", lines).TrimEnd() + "\r\n", new UTF8Encoding(false));
        }
        var target = Path.Combine(dir, "soundflow.txt");
        if (File.Exists(target)) File.Delete(target);
    }

    static void RunHidden(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(15000);
    }

    /// <summary>
    /// Is Equalizer APO attached to this render endpoint? null = could not tell.
    /// deviceId looks like "{0.0.0.00000000}.{guid}".
    /// </summary>
    public static bool? IsAttached(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;
        try
        {
            string guid = deviceId[(deviceId.LastIndexOf('.') + 1)..];
            using (var child = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\EqualizerAPO\Child APOs\{guid}"))
                if (child is not null) return true;
            using var fx = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{guid}\FxProperties");
            if (fx is null) return null;
            foreach (var name in fx.GetValueNames())
            {
                var value = fx.GetValue(name);
                string text = value switch { string s => s, string[] a => string.Join(";", a), _ => "" };
                if (text.Contains("EACD2258-FCAC-4FF4-B36D-419E924A6D79", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch (Exception ex) { Log.Error("Проверка подключения APO", ex); return null; }
    }
}

/// <summary>RBJ biquad magnitude responses, used by the graph and the APO writer.</summary>
internal static class Filters
{
    const double Fs = 48000;

    /// <summary>Q values of cascaded 2nd-order sections for LP/HP. 12 dB/oct = one section with the user's Q.</summary>
    public static double[] SectionQs(Band b)
    {
        int n = Math.Max(1, b.Slope / 12);
        if (n == 1) return [0.7071];
        var qs = new double[n];
        for (int k = 1; k <= n; k++) qs[k - 1] = 1 / (2 * Math.Cos((2 * k - 1) * Math.PI / (4 * n)));
        return qs;
    }

    public static double MagnitudeDb(Band b, double f)
    {
        if (b.Type is "LP" or "HP")
        {
            double sum = 0;
            foreach (var q in SectionQs(b)) sum += Section(b.Type, b.Frequency, b.Gain, q, f);
            return sum;
        }
        return Section(b.Type, b.Frequency, b.Gain, b.Q, f);
    }

    static double Section(string type, double frequency, double gain, double qIn, double f)
    {
        double fc = Math.Clamp(frequency, 10, 22000), q = Math.Clamp(qIn, .1, 30), A = Math.Pow(10, gain / 40),
               w = 2 * Math.PI * fc / Fs, c = Math.Cos(w), s = Math.Sin(w), alpha = s / (2 * q), b0, b1, b2, a0, a1, a2;
        switch (type)
        {
            case "LP": b0 = (1 - c) / 2; b1 = 1 - c; b2 = b0; a0 = 1 + alpha; a1 = -2 * c; a2 = 1 - alpha; break;
            case "HP": b0 = (1 + c) / 2; b1 = -(1 + c); b2 = b0; a0 = 1 + alpha; a1 = -2 * c; a2 = 1 - alpha; break;
            case "NO": b0 = 1; b1 = -2 * c; b2 = 1; a0 = 1 + alpha; a1 = -2 * c; a2 = 1 - alpha; break;
            case "BP": b0 = alpha; b1 = 0; b2 = -alpha; a0 = 1 + alpha; a1 = -2 * c; a2 = 1 - alpha; break;
            case "LS":
            {
                double t = 2 * Math.Sqrt(A) * alpha;
                b0 = A * ((A + 1) - (A - 1) * c + t); b1 = 2 * A * ((A - 1) - (A + 1) * c); b2 = A * ((A + 1) - (A - 1) * c - t);
                a0 = (A + 1) + (A - 1) * c + t; a1 = -2 * ((A - 1) + (A + 1) * c); a2 = (A + 1) + (A - 1) * c - t; break;
            }
            case "HS":
            {
                double t = 2 * Math.Sqrt(A) * alpha;
                b0 = A * ((A + 1) + (A - 1) * c + t); b1 = -2 * A * ((A - 1) + (A + 1) * c); b2 = A * ((A + 1) + (A - 1) * c - t);
                a0 = (A + 1) - (A - 1) * c + t; a1 = 2 * ((A - 1) - (A + 1) * c); a2 = (A + 1) - (A - 1) * c - t; break;
            }
            default:
                b0 = 1 + alpha * A; b1 = -2 * c; b2 = 1 - alpha * A; a0 = 1 + alpha / A; a1 = -2 * c; a2 = 1 - alpha / A; break;
        }
        double wf = 2 * Math.PI * f / Fs;
        Complex z = Complex.FromPolarCoordinates(1, -wf), z2 = z * z;
        Complex num = b0 + b1 * z + b2 * z2, den = a0 + a1 * z + a2 * z2;
        return 20 * Math.Log10(Math.Max(1e-8, num.Magnitude / den.Magnitude));
    }

    /// <summary>Highest point of the combined curve (dB), used for headroom display and auto-compensation.</summary>
    public static double PeakDb(IEnumerable<Band> bands)
    {
        var active = bands.Where(b => b.Enabled).ToList();
        if (active.Count == 0) return 0;
        double peak = double.MinValue;
        for (int i = 0; i < 240; i++)
        {
            double f = 20 * Math.Pow(1000, i / 239d), db = 0;
            foreach (var b in active) db += MagnitudeDb(b, f);
            peak = Math.Max(peak, db);
        }
        return peak;
    }
}
