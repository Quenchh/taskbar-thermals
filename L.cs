using System.Globalization;

namespace TaskbarThermals;

/// <summary>
/// UI strings. English is the default; Turkish is used when the Windows display language is Turkish
/// (or when chosen in settings). Everything user-visible goes through here.
/// </summary>
internal static class L
{
    private static readonly CultureInfo TrCulture = CultureInfo.GetCultureInfo("tr-TR");

    public static bool Turkish { get; private set; }

    /// <summary>Number formatting that matches the UI language.</summary>
    public static CultureInfo Culture => Turkish ? TrCulture : CultureInfo.InvariantCulture;

    /// <param name="language">"auto", "en" or "tr".</param>
    public static void Apply(string? language) => Turkish = language switch
    {
        "tr" => true,
        "en" => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr",
    };

    private static string T(string en, string tr) => Turkish ? tr : en;

    // ---------------------------------------------------------------- formatting

    public static string Percent(float v) => Turkish ? $"%{v:0}" : $"{v:0}%";
    public static string Number(float v, string format) => v.ToString(format, Culture);
    public static string When(DateTime t) => Turkish ? t.ToString("d MMM HH:mm", TrCulture) : t.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Compact byte rate for the taskbar: 850K, 1.2M, 35M, 1.1G (per second).</summary>
    public static string RateShort(float bytesPerSecond)
    {
        double kb = bytesPerSecond / 1024.0;
        if (kb < 999.5) return $"{kb.ToString("0", Culture)}K";
        double mb = kb / 1024;
        if (mb < 9.95) return $"{mb.ToString("0.0", Culture)}M";
        if (mb < 999.5) return $"{mb.ToString("0", Culture)}M";
        return $"{(mb / 1024).ToString("0.0", Culture)}G";
    }

    /// <summary>Byte rate for the panel: 850 KB/s, 12.3 MB/s.</summary>
    public static string Rate(float bytesPerSecond)
    {
        double kb = bytesPerSecond / 1024.0;
        if (kb < 999.5) return $"{kb.ToString("0", Culture)} KB/s";
        double mb = kb / 1024;
        if (mb < 999.5) return $"{mb.ToString(mb < 9.95 ? "0.0" : "0", Culture)} MB/s";
        return $"{(mb / 1024).ToString("0.0", Culture)} GB/s";
    }

    // ---------------------------------------------------------------- notifications

    public static string CpuHotTitle => T("CPU running hot", "CPU çok ısındı");
    public static string GpuHotTitle => T("GPU running hot", "GPU çok ısındı");
    public static string HotText(string device, float temp, int limit, int seconds) => T(
        $"{device} at {temp:0}°C, above {limit}°C for {seconds} seconds.",
        $"{device} {temp:0}°C, {seconds} saniyedir {limit}°C üstünde.");

    public static string StabilityTitle => T("Stability warning", "Kararlılık uyarısı");
    public static string StabilitySummary(int count, string latest, DateTime at) => count == 1
        ? $"{latest} ({When(at)})."
        : T($"{count} events. Latest: {latest} ({When(at)}).", $"{count} olay. En sonuncusu: {latest} ({When(at)}).");
    public static string StabilityLive(string title, DateTime at) => T(
        $"{title} at {at:HH:mm}. A recent BIOS change (undervolt, Curve Optimizer, SoC voltage) may be unstable.",
        $"{title} ({at:HH:mm}). Son BIOS ayarı (undervolt, Curve Optimizer, SoC voltajı) kararsız olabilir.");

    public static string UpdateTitle => T("Update available", "Güncelleme var");
    public static string UpdateText(string version) => T(
        $"Taskbar Thermals {version} is out. Click to open the download page.",
        $"Taskbar Thermals {version} çıktı. İndirme sayfasını açmak için tıkla.");

    // ---------------------------------------------------------------- stability events

    public static string EventTitle(StabilityEvent e) => e.Kind switch
    {
        StabilityKind.Whea => e.WheaId switch
        {
            18 => T("Fatal hardware error (WHEA 18)", "Ölümcül donanım hatası (WHEA 18)"),
            19 => T("Corrected CPU error (WHEA 19)", "Düzeltilmiş işlemci hatası (WHEA 19)"),
            47 => T("Corrected memory error (WHEA 47)", "Düzeltilmiş bellek hatası (WHEA 47)"),
            17 => T("Corrected PCIe error (WHEA 17)", "Düzeltilmiş PCIe hatası (WHEA 17)"),
            var id => T($"Hardware error (WHEA {id})", $"Donanım hatası (WHEA {id})"),
        },
        StabilityKind.UnexpectedShutdown => T("Unexpected shutdown or restart", "Beklenmedik kapanma veya yeniden başlama"),
        _ => T("Blue screen (BugCheck)", "Mavi ekran (BugCheck)"),
    };

    // ---------------------------------------------------------------- menu

    public static string MenuMax(string cpu, string gpu) => T($"Max CPU: {cpu}     Max GPU: {gpu}", $"Maks. CPU: {cpu}     Maks. GPU: {gpu}");
    public static string MenuLastSpike(string spike) => T($"Last spike: {spike}", $"Son sıçrama: {spike}");
    public static string MenuNoSpikes => T("No spikes logged yet", "Henüz sıçrama kaydı yok");
    public static string MenuStability(int count, string latest) => T($"Stability: {count} event(s), latest: {latest}", $"Kararlılık: {count} olay, son: {latest}");
    public static string MenuNoCpuTemp => T("Can't read CPU temperature — run as administrator", "CPU sıcaklığı okunamıyor — yönetici olarak çalıştırın");
    public static string MenuUpdate(string version) => T($"Update available: {version}", $"Güncelleme var: {version}");
    public static string MenuDetails => T("Details", "Detay paneli");
    public static string TaskManager => T("Task Manager", "Görev Yöneticisi");
    public static string MenuShowSpikeLog => T("Show spike log", "Sıçrama kaydını göster");
    public static string MenuSettings => T("Settings…", "Ayarlar…");
    public static string MenuResetMax => T("Reset max values", "Maks. değerleri sıfırla");
    public static string MenuResetPosition => T("Reset position", "Konumu sıfırla");
    public static string StartWithWindows => T("Start with Windows", "Windows ile başlat");
    public static string MenuExit => T("Exit", "Kapat");
    public static string StartupFailed => T(
        "Couldn't update the Task Scheduler entry. Make sure the app is running as administrator.",
        "Görev Zamanlayıcı kaydı değiştirilemedi. Programın yönetici olarak çalıştığından emin olun.");

    // ---------------------------------------------------------------- panel

    public static string Temp => T("Temp", "Sıcaklık");
    public static string High => T("High", "Yüksek");
    public static string Critical => T("Critical", "Kritik");
    public static string Usage => T("Usage", "Kullanım");
    public static string Power => T("Power", "Güç");
    public static string Clock => T("Clock", "Saat");
    public static string MemClock => T("Mem clock", "Bellek saati");
    public static string Temperature => T("Temperature", "Sıcaklık");
    public static string History => T("History", "Geçmiş");
    public static string RangeName(HistoryRange r) => r switch
    {
        HistoryRange.Hour => T("1 h", "1 sa"),
        HistoryRange.Day => T("24 h", "24 sa"),
        _ => T("10 min", "10 dk"),
    };
    public static string RangeStart(HistoryRange r) => r switch
    {
        HistoryRange.Hour => T("1 h ago", "1 sa önce"),
        HistoryRange.Day => T("24 h ago", "24 sa önce"),
        _ => T("10 min ago", "10 dk önce"),
    };
    public static string RangeMiddle(HistoryRange r) => r switch
    {
        HistoryRange.Hour => T("30 min", "30 dk"),
        HistoryRange.Day => T("12 h", "12 sa"),
        _ => T("5 min", "5 dk"),
    };
    public static string Now => T("now", "şimdi");
    public static string MaxShort => T("max", "maks.");
    public static string System => T("System", "Sistem");
    public static string SocVoltage => T("SoC voltage", "SoC voltajı");
    public static string GpuMemory => T("GPU memory", "GPU bellek");
    public static string Download => T("Download", "İndirme");
    public static string Upload => T("Upload", "Yükleme");
    public static string DiskRead => T("Disk read", "Disk okuma");
    public static string DiskWrite => T("Disk write", "Disk yazma");
    public static string Sensors => T("Sensors", "Sensörler");
    public static string NeedAdmin => T("need admin rights", "yönetici izni gerekli");
    public static string FanName(string lhmName) => lhmName switch
    {
        "CPU Fan" => T("CPU fan", "CPU fanı"),
        "Chipset Fan" => T("Chipset fan", "Chipset fanı"),
        _ when lhmName.StartsWith("System Fan") => T("Case fan", "Kasa fanı") + lhmName["System Fan".Length..].Replace("#", ""),
        _ when lhmName.StartsWith("Pump Fan") => T("Pump", "Pompa") + lhmName["Pump Fan".Length..].Replace("#", ""),
        _ => lhmName,
    };
    public static string Stability => T("Stability", "Kararlılık");
    public static string MarkSeen => T("Mark as seen", "Gördüm, sıfırla");
    public static string EventLogUnavailable => T("Event log unavailable", "Olay günlüğü okunamıyor");
    public static string NoHardwareErrors => T("No hardware errors", "Donanım hatası yok");
    public static string EventsLogged(int n) => T(n == 1 ? "1 event logged" : $"{n} events logged", $"{n} olay kaydedildi");
    public static string Since(DateTime t) => T($"· since {When(t)}", $"· {When(t)} itibarıyla");
    public static string LastSevenDays => T("· last 7 days", "· son 7 gün");
    public static string More(int n) => T($"+{n} more", $"+{n} olay daha");
    public static string FooterMax(string cpu, string gpu) => T($"Max CPU {cpu}   ·   GPU {gpu}", $"Maks. CPU {cpu}   ·   GPU {gpu}");
    public static string SpikeLog => T("Spike log", "Sıçrama kaydı");
    public static string Settings => T("Settings", "Ayarlar");

    // ---------------------------------------------------------------- settings window

    public static string SettingsTitle => T("Taskbar Thermals Settings", "Taskbar Thermals Ayarları");
    public static string Taskbar => T("Taskbar", "Görev çubuğu");
    public static string ShowOnTaskbar => T("Show on the taskbar:", "Görev çubuğunda göster:");
    public static string MetricName(string id) => id switch
    {
        "cpu.temp" or "gpu.temp" => T("Temp", "Sıcaklık"),
        "cpu.load" or "gpu.load" or "ram.load" => T("Usage %", "Kullanım %"),
        "cpu.power" or "gpu.power" => T("Power (W)", "Güç (W)"),
        "cpu.clock" => T("Clock", "Saat"),
        "gpu.vram" => T("Memory temp", "Bellek sıcaklığı"),
        "ram.used" => T("Used (GB)", "Kullanılan (GB)"),
        "net.down" => T("Download", "İndirme"),
        "net.up" => T("Upload", "Yükleme"),
        "disk.read" => T("Read", "Okuma"),
        "disk.write" => T("Write", "Yazma"),
        _ => id,
    };
    public static string DeviceName(Device d) => d switch
    {
        Device.Ram => "RAM",
        Device.Net => T("Network", "Ağ"),
        Device.Disk => T("Disk", "Disk"),
        _ => d.ToString().ToUpperInvariant(),
    };
    public static string FontSize => T("Font size", "Yazı boyutu");
    public static string RefreshEvery => T("Refresh every", "Yenileme aralığı");
    public static string Seconds(string s) => T($"{s} s", $"{s} sn");
    public static string ColorThresholds => T("Color thresholds (°C)", "Renk eşikleri (°C)");
    public static string Warning => T("warning", "sarı");
    public static string CriticalLower => T("critical", "kırmızı");
    public static string Notifications => T("Notifications", "Bildirimler");
    public static string NotifyWhen(string device) => T($"Notify when {device} exceeds", $"{device} şunu aşarsa bildir:");
    public static string AboveFor => T("when above that for", "en az");
    public static string SecondsWord => T("seconds", "saniye sürerse");
    public static string StabilityAlerts => T("Hardware errors, blue screens and unexpected shutdowns", "Donanım hatası, mavi ekran ve beklenmedik kapanmalar");
    public static string LogWhenRises => T("Log when CPU rises", "CPU 5 saniyede");
    public static string WithinFive => T("°C within 5 seconds", "°C artarsa kaydet");
    public static string General => T("General", "Genel");
    public static string Language => T("Language", "Dil");
    public static string Automatic => T("Automatic", "Otomatik");
    public static string CheckUpdates => T("Check for updates (once a day, via GitHub)", "Güncellemeleri denetle (günde bir, GitHub üzerinden)");
    public static string StartWithWindowsLong => T("Start with Windows (as administrator, without a UAC prompt)", "Windows ile başlat (yönetici olarak, UAC sormadan)");
    public static string Save => T("Save", "Kaydet");
    public static string Cancel => T("Cancel", "İptal");
    public static string ThresholdOrder => T("The warning threshold must be lower than the critical one.", "Sarı eşik, kırmızı eşikten küçük olmalı.");
    public static string PickOneMetric => T("Pick at least one value to show on the taskbar.", "Görev çubuğunda en az bir değer seç.");
}
