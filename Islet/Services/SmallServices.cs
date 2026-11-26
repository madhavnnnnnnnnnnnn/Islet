using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Islet.Services;

// ───────────────────────── Battery ─────────────────────────
public sealed class PowerService
{
    readonly Dispatcher _d;
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(60) };
    bool _lastOnline, _warned20, _warned10;

    public bool HasBattery { get; private set; }
    public bool Charging { get; private set; }
    public int Percent { get; private set; }

    public event Action? PluggedIn, Changed;
    public event Action<int>? Low;

    public PowerService(Dispatcher d) { _d = d; }

    public void Start()
    {
        Read();
        _lastOnline = Charging;
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.StatusChange) _d.InvokeAsync(Update);
        };
        _t.Tick += (_, _) => Update();
        _t.Start();
    }

    void Read()
    {
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        HasBattery = (ps.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0
                     && ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.Unknown;
        Charging = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
        Percent = (int)Math.Round(Math.Clamp(ps.BatteryLifePercent, 0f, 1f) * 100);
    }

    void Update()
    {
        Read();
        if (!HasBattery) return;
        if (Charging && !_lastOnline) PluggedIn?.Invoke();
        _lastOnline = Charging;

        if (Charging) { _warned20 = _warned10 = false; }
        else if (Percent <= 10 && !_warned10) { _warned10 = _warned20 = true; Low?.Invoke(Percent); }
        else if (Percent <= 20 && !_warned20) { _warned20 = true; Low?.Invoke(Percent); }
        Changed?.Invoke();
    }
}

// ───────────────────────── Bluetooth ─────────────────────────
public sealed class BluetoothService
{
    readonly Dispatcher _d;
    DeviceWatcher? _w;
    readonly Dictionary<string, string> _names = new();
    readonly Dictionary<string, DateTime> _recent = new();
    bool _ready;

    public event Action<string, bool>? DeviceChanged; // name, connected

    public BluetoothService(Dispatcher d) { _d = d; }

    public void Start()
    {
        try
        {
            var sel = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            _w = DeviceInformation.CreateWatcher(sel);
            _w.Added += (_, di) => _d.InvokeAsync(() =>
            {
                _names[di.Id] = di.Name;
                if (_ready) Raise(di.Name, true);
            });
            _w.Removed += (_, u) => _d.InvokeAsync(() =>
            {
                if (_names.Remove(u.Id, out var n) && _ready) Raise(n, false);
            });
            _w.Updated += (_, _) => { };
            _w.EnumerationCompleted += (_, _) => _d.InvokeAsync(() => _ready = true);
            _w.Start();
        }
        catch { /* no bluetooth adapter */ }
    }

    void Raise(string name, bool connected)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string key = name + connected;
        if (_recent.TryGetValue(key, out var t) && (DateTime.Now - t).TotalSeconds < 10) return; // de-bounce flapping devices
        _recent[key] = DateTime.Now;
        DeviceChanged?.Invoke(name, connected);
    }
}

// ───────────────────────── Privacy dots + fullscreen ─────────────────────────
public sealed class SystemWatch
{
    const string Consent = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly IntPtr _own;
    bool _mic, _cam, _fs;

    public event Action<bool, bool>? PrivacyChanged; // mic, camera
    public event Action<bool>? FullscreenChanged;
    public event Action? Polled;

    public SystemWatch(IntPtr own) { _own = own; }

    public void Start() { _t.Tick += (_, _) => Poll(); _t.Start(); Poll(); }

    void Poll()
    {
        bool mic = false, cam = false, fs = false;
        try { mic = InUse("microphone"); cam = InUse("webcam"); } catch { }
        try { fs = Native.IsForegroundFullscreen(_own); } catch { }
        if (mic != _mic || cam != _cam) { _mic = mic; _cam = cam; PrivacyChanged?.Invoke(mic, cam); }
        if (fs != _fs) { _fs = fs; FullscreenChanged?.Invoke(fs); }
        Polled?.Invoke();
    }

    static bool InUse(string cap)
    {
        using var root = Registry.CurrentUser.OpenSubKey(Consent + cap);
        if (root == null) return false;
        if (Check(root)) return true;
        using var np = root.OpenSubKey("NonPackaged");
        return np != null && Check(np);

        static bool Check(RegistryKey k)
        {
            foreach (var name in k.GetSubKeyNames())
            {
                if (name == "NonPackaged") continue;
                using var sk = k.OpenSubKey(name);
                if (sk?.GetValue("LastUsedTimeStart") is long start && start > 0 &&
                    sk.GetValue("LastUsedTimeStop") is long stop && stop == 0) return true;
            }
            return false;
        }
    }
}

// ───────────────────────── Settings ─────────────────────────
public sealed class Settings
{
    public bool Lyrics { get; set; } = true;
    public bool AutoHide { get; set; } = true;
    public bool Glass { get; set; } = true;
    public bool ShowIdlePill { get; set; } = true;
    public bool HoverExpand { get; set; } = true;
    public bool BluetoothAlerts { get; set; } = true;
    public bool BatteryAlerts { get; set; } = true;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Islet");
    static string FilePath => Path.Combine(Dir, "settings.json");
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static Settings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static bool RunAtStartup
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Islet") != null; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("Islet", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("Islet", false);
        }
    }
}
