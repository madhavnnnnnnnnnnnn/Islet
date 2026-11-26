using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Islet.Services;

public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>Synced lyrics from LRCLIB (free, no API key). Results are cached in memory per track.</summary>
public sealed partial class LyricsService
{
    static readonly HttpClient Http = CreateClient();
    [GeneratedRegex(@"\[(\d+):(\d+(?:[.:]\d+)?)\]")] private static partial Regex StampRx();
    [GeneratedRegex(@"\s*[\(\[](feat\.?|ft\.?|with|official|lyric|lyrics|audio|video|visualizer|remaster|hd|4k|mv|explicit)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex TagsRx();
    [GeneratedRegex(@"\s+-\s+(\d{4}\s+)?remaster(ed)?.*$", RegexOptions.IgnoreCase)] private static partial Regex RemasterRx();
    static readonly Regex Stamp = StampRx();
    readonly Dictionary<string, List<LyricLine>?> _cache = new();

    static HttpClient CreateClient()
    {
        // Close idle connections quickly so TLS buffers don't linger in memory between songs
        var h = new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Islet/1.0 (desktop lyrics widget)");
        return c;
    }

    public async Task<List<LyricLine>?> GetAsync(string artist, string title, TimeSpan duration)
    {
        string key = (artist + "|" + title).ToLowerInvariant();
        if (_cache.TryGetValue(key, out var cached)) return cached;

        List<LyricLine>? result = null;
        try
        {
            string clean = Clean(title);
            result = await Search($"track_name={E(clean)}&artist_name={E(artist)}", duration);

            // YouTube-style "Artist - Song" titles
            if (result == null && title.Contains(" - "))
            {
                var parts = title.Split(" - ", 2);
                result = await Search($"track_name={E(Clean(parts[1]))}&artist_name={E(parts[0].Trim())}", duration);
            }
            result ??= await Search($"q={E((clean + " " + artist).Trim())}", duration);
        }
        catch { /* offline etc. -> no lyrics */ }

        if (_cache.Count > 200) _cache.Clear();
        _cache[key] = result;
        return result;
    }

    static async Task<List<LyricLine>?> Search(string query, TimeSpan duration)
    {
        using var resp = await Http.GetAsync("https://lrclib.net/api/search?" + query);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        string? best = null; double bestDiff = double.MaxValue;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("syncedLyrics", out var sl) || sl.ValueKind != JsonValueKind.String) continue;
            var text = sl.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            double diff = 0;
            if (duration.TotalSeconds > 1 && item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
                diff = Math.Abs(d.GetDouble() - duration.TotalSeconds);
            if (diff < bestDiff) { bestDiff = diff; best = text; }
        }
        if (best == null || bestDiff > 12) return null;
        return Parse(best);
    }

    static List<LyricLine>? Parse(string lrc)
    {
        var list = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var ms = Stamp.Matches(raw);
            if (ms.Count == 0) continue;
            string text = Stamp.Replace(raw, "").Trim();
            foreach (Match m in ms)
            {
                int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                list.Add(new LyricLine(TimeSpan.FromSeconds(min * 60 + sec), text));
            }
        }
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list.Count > 0 ? list : null;
    }

    static string Clean(string t)
    {
        t = TagsRx().Replace(t, "");
        t = RemasterRx().Replace(t, "");
        return t.Trim();
    }

    static string E(string s) => Uri.EscapeDataString(s ?? "");
}
