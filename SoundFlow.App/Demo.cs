namespace SoundFlow;

/// <summary>
/// Sample data for README screenshots (`SoundFlow.exe --screenshots &lt;folder&gt;`, run by GitHub Actions).
/// Nothing here touches Equalizer APO, the registry or the user's settings.
/// </summary>
internal static class Demo
{
    public static DeviceInfo Device => new("demo-realtek", "Realtek(R) Audio", 6);

    public static List<OutputDevice> Outputs =>
    [
        new("demo-realtek", "Realtek(R) Audio", 6, 48000, true),
        new("demo-usb", "USB Audio Device", 2, 48000, false),
        new("demo-hdmi", "LG HDR 4K (NVIDIA High Definition Audio)", 2, 48000, false),
    ];

    public static Settings Settings()
    {
        var s = new Settings
        {
            Preamp = 6,
            Mode = 1,
            Profile = "flat",
            DisplayRange = 12,
            MainDelayMs = 40,
            Graphic = [4, 3.5, 2, 0.5, -1, -0.5, 1, 2.5, 3, 2],
            Parametric =
            [
                new() { Type = "HP", Frequency = 28, Q = .71, Slope = 24 },
                new() { Type = "LS", Frequency = 95, Gain = 4.5, Q = .7 },
                new() { Type = "PK", Frequency = 320, Gain = -3.5, Q = 1.4 },
                new() { Type = "PK", Frequency = 2600, Gain = 2.5, Q = 1.1 },
                new() { Type = "NO", Frequency = 6300, Q = 8 },
                new() { Type = "HS", Frequency = 9000, Gain = 2, Q = .7 },
            ],
        };
        s.Channels[4] = new ChannelTrim { Gain = 3 };
        s.Channels[3] = new ChannelTrim { Gain = 1.5 };
        // 5.1 film: front, center and sub stay on the main card, the rear pair goes to a USB card
        s.Routes["demo-realtek"] = new RouteTarget { Name = "Realtek(R) Audio", Custom = true, Pairs = ["1-1", "2-2", "3-3", "4-4"] };
        s.Routes["demo-usb"] = new RouteTarget { Name = "USB Audio Device", Pairs = ["5-1", "6-2"], Gain = -2 };
        s.Routes["demo-hdmi"] = new RouteTarget { Name = "LG HDR 4K", Pairs = ["1-1", "2-2"] };
        s.Normalize();
        return s;
    }

    /// <summary>A plausible music spectrum (pink-ish with a few peaks).</summary>
    public static SpectrumFrame Spectrum()
    {
        const int n = 4096, rate = 48000;
        var values = new float[n / 2];
        var rnd = new Random(7);
        for (int i = 1; i < values.Length; i++)
        {
            double f = i * (double)rate / n;
            double db = -28 - 10 * Math.Log10(f / 100) - (f > 12000 ? (f - 12000) / 600 : 0);
            db += 6 * Math.Exp(-Math.Pow(Math.Log(f / 60), 2) * 4) + 4 * Math.Exp(-Math.Pow(Math.Log(f / 2400), 2) * 6);
            db += rnd.NextDouble() * 4 - 2;
            values[i] = (float)db;
        }
        values[0] = values[1];
        return new SpectrumFrame(values, rate, n);
    }
}
