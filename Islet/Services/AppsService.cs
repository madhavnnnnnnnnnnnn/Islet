using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Islet.Services;

public sealed class AppShortcut
{
    public string Name { get; set; } = "";
    /// <summary>An .exe/.lnk path, or "shell:AppsFolder\{AUMID}" for Store apps.</summary>
    public string Target { get; set; } = "";
}

/// <summary>
/// 3–5 app shortcuts shown in the island. First run seeds them from Windows' own launch counts
/// (UserAssist); after that the list is yours: drop apps onto the island, right-click to remove.
/// </summary>
public sealed class AppsService
{
    public const int Max = 5;
    static string FilePath => Path.Combine(Settings.Dir, "apps.json");
    readonly Dictionary<string, ImageSource?> _icons = new();

    public List<AppShortcut> Apps { get; private set; } = new();
    public event Action? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Apps = JsonSerializer.Deserialize<List<AppShortcut>>(File.ReadAllText(FilePath)) ?? new();
                return;
            }
        }
        catch { }
        ResetToMostUsed();
    }

    public void ResetToMostUsed()
    {
        try { Apps = MostUsed(Max); } catch { Apps = new(); }
        Save();
        Changed?.Invoke();
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Apps, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <returns>false when the list is already full or the target is invalid.</returns>
    public bool Add(string target)
    {
        if (Apps.Count >= Max) return false;
        if (!target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && !File.Exists(target)) return false;
        if (Apps.Any(a => string.Equals(a.Target, target, StringComparison.OrdinalIgnoreCase))) return true;
        Apps.Add(new AppShortcut { Name = NiceName(target), Target = target });
        Save();
        Changed?.Invoke();
        return true;
    }

    public void Remove(AppShortcut a)
    {
        Apps.Remove(a);
        Save();
        Changed?.Invoke();
    }

    public static void Launch(AppShortcut a)
    {
        try
        {
            if (a.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("explorer.exe", a.Target) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(a.Target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(a.Target) ?? "" });
        }
        catch { }
    }

    static string NiceName(string target)
    {
        if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            var n = ShellDisplayName(target);
            if (!string.IsNullOrEmpty(n)) return n!;
            return target.Split('!').Last();
        }
        try
        {
            if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var d = FileVersionInfo.GetVersionInfo(target).FileDescription;
                if (!string.IsNullOrWhiteSpace(d) && d.Length < 40) return d.Trim();
            }
        }
        catch { }
        return Path.GetFileNameWithoutExtension(target);
    }

    // ───────────── icons (works for exe, lnk and Store apps via the shell) ─────────────

    public ImageSource? Icon(AppShortcut a)
    {
        if (_icons.TryGetValue(a.Target, out var cached)) return cached;
        ImageSource? img = null;
        try { img = ShellImage(a.Target, 64); } catch { }
        _icons[a.Target] = img;
        return img;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbmp); }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    static ImageSource? ShellImage(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object o);
        var f = (IShellItemImageFactory)o;
        // SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY
        if (f.GetImage(new SIZE { cx = size, cy = size }, 0x1 | 0x4, out IntPtr hbmp) != 0) return null;
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            // Read raw 32-bit premultiplied pixels (WPF's HBITMAP import would drop the alpha channel).
            GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bm);
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            var bi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var px = new byte[w * h * 4];
            if (GetDIBits(dc, hbmp, 0, (uint)h, px, ref bi, 0) == 0) return null;
            bool anyAlpha = false;
            for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
            bs.Freeze();
            return bs;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); Marshal.ReleaseComObject(f); DeleteObject(hbmp); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);

    static string? ShellDisplayName(string path)
    {
        try
        {
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object o);
            ((IShellItem)o).GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var name);
            return name;
        }
        catch { return null; }
    }

    // ───────────── "most used" from Windows UserAssist launch counts ─────────────

    static readonly string[] Skip =
    {
        "explorer.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "rundll32.exe", "mmc.exe", "msiexec.exe",
        "setup", "install", "uninst", "update", "islet.exe", "control.exe", "systemsettings", "applicationframehost",
        "searchhost", "shellexperiencehost", "lockapp", "regedit", "taskmgr", "notepad.exe", "snippingtool", "dllhost",
        "screensketch", "snipping tool", "apprep", "file explorer", "microsoft store", "windowsstore",
    };

    /// <summary>
    /// Ranks apps by how much you use them: Windows' UserAssist launch counts + focus time,
    /// plus a bonus for apps pinned to the taskbar. Duplicates (same app via different routes) merge by name.
    /// </summary>
    static List<AppShortcut> MostUsed(int count)
    {
        var found = new List<(string target, double score)>();

        const string key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count";
        using (var k = Registry.CurrentUser.OpenSubKey(key))
        {
            foreach (var raw in k?.GetValueNames() ?? Array.Empty<string>())
            {
                if (k!.GetValue(raw) is not byte[] data || data.Length < 68) continue;
                int runs = BitConverter.ToInt32(data, 4);
                int focusMs = BitConverter.ToInt32(data, 12);
                double score = runs * 4 + focusMs / 60000.0; // a launch ≈ 4 minutes of use
                if (score < 1) continue;

                string? target = Resolve(Rot13(raw));
                if (target != null) found.Add((target, score));
            }
        }

        // Taskbar pins are a strong "I use this a lot" signal
        try
        {
            var pins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (Directory.Exists(pins))
                foreach (var lnk in Directory.GetFiles(pins, "*.lnk")) found.Add((lnk, 30));
        }
        catch { }

        return found
            .Where(f => !Skip.Any(f.target.ToLowerInvariant().Contains))
            .Select(f => (f.target, f.score, name: NiceName(f.target)))
            .Where(f => !string.IsNullOrWhiteSpace(f.name) && !Skip.Any(f.name.ToLowerInvariant().Contains))
            .GroupBy(f => f.name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (name: g.Key, score: g.Sum(x => x.score),
                          // prefer launching through a real .lnk/.exe over an app ID when we have both
                          target: g.OrderBy(x => x.target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)).First().target))
            .OrderByDescending(f => f.score)
            .Take(count)
            .Select(f => new AppShortcut { Name = f.name, Target = f.target })
            .ToList();
    }

    static string? Resolve(string name)
    {
        if (name.StartsWith("UEME_") || name.StartsWith("Chrome.UserData", StringComparison.OrdinalIgnoreCase)) return null;

        // Apps recorded by app ID (AUMID): "Chrome", "Microsoft.VisualStudioCode", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"
        if (!name.Contains('\\') && !name.Contains(':'))
        {
            var t = @"shell:AppsFolder\" + name;
            return ShellDisplayName(t) != null ? t : null; // only if the shell knows it (i.e. still installed)
        }
        if (name.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return null; // covered by the AUMID entry

        // Desktop apps: "{KNOWNFOLDERID}\Sub\Path\app.exe"
        if (name.StartsWith("{") && name.Length > 38 && name[38] == '}')
        {
            try
            {
                var id = new Guid(name.Substring(1, 36));
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var root) == 0)
                    name = root + name.Substring(39);
            }
            catch { return null; }
        }
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(name) ? name : null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out string path);

    static string Rot13(string s)
    {
        var c = s.ToCharArray();
        for (int i = 0; i < c.Length; i++)
        {
            char ch = c[i];
            if (ch is >= 'a' and <= 'z') c[i] = (char)('a' + (ch - 'a' + 13) % 26);
            else if (ch is >= 'A' and <= 'Z') c[i] = (char)('A' + (ch - 'A' + 13) % 26);
        }
        return new string(c);
    }
}
