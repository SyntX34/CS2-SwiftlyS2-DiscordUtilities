using System.Net;
using System.Text.Json;
namespace DiscordUtilities.Services;
public static class DiscordPlayerFormat
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    public static string PlayerLink(string name, ulong steamId, string? country = null)
    {
        var safe = name.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]").Replace("@", "＠").Replace("\n", " ").Replace("\r", " ");
        var flag = Flag(country);
        return steamId == 0 ? $"{flag} {safe}" : $"{flag} [{safe}](<https://steamcommunity.com/profiles/{steamId}>)";
    }
    public static string Flag(string? code)
    {
        if (code?.Length != 2 || !code.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')) return "🌐";
        return string.Concat(code.ToUpperInvariant().Select(c => char.ConvertFromUtf32(0x1F1E6 + c - 'A')));
    }
    public static async Task<string?> LookupCountryAsync(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var raw = ip.Trim().Trim('[', ']');
        if (raw.Contains(':') && raw.Count(c => c == ':') == 1) raw = raw.Split(':')[0];
        if (!System.Net.IPAddress.TryParse(raw, out var parsed) || System.Net.IPAddress.IsLoopback(parsed) ||
            (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
             (parsed.GetAddressBytes()[0] is 10 or 127 ||
              parsed.GetAddressBytes()[0] == 192 && parsed.GetAddressBytes()[1] == 168 ||
              parsed.GetAddressBytes()[0] == 172 && parsed.GetAddressBytes()[1] is >= 16 and <= 31))) return null;
        try
        {
            using var response = await Http.GetAsync($"https://ipwho.is/{Uri.EscapeDataString(raw)}");
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("country_code", out var c) ? c.GetString() : null;
        }
        catch { return null; }
    }
}
