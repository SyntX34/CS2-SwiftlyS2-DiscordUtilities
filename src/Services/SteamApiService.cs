using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DiscordUtilities.Services;

public sealed class SteamApiService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private string _apiKey = "";

    private readonly ConcurrentDictionary<ulong, string> _avatarCache = new();
    private readonly ConcurrentDictionary<ulong, WorkshopMapInfo?> _workshopCache = new();
    private readonly ConcurrentDictionary<string, bool> _standardMapCheckCache = new();

    public SteamApiService(ILogger logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DiscordUtilities-SwiftlyS2/1.0");
    }

    public void SetApiKey(string apiKey) => _apiKey = apiKey;

    public void ClearCaches() => _avatarCache.Clear();

    public async Task<string?> GetStandardMapImageAsync(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName)) return null;

        var url = $"https://vauff.com/mapimgs/730_cs2/{mapName}.jpg";

        if (_standardMapCheckCache.TryGetValue(mapName, out var exists))
        {
            return exists ? url : null;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            var response = await _httpClient.SendAsync(req);
            var ok = response.IsSuccessStatusCode;
            _standardMapCheckCache[mapName] = ok;
            return ok ? url : null;
        }
        catch
        {
            _standardMapCheckCache[mapName] = false;
            return null;
        }
    }

    public async Task<string?> GetPlayerAvatarAsync(ulong steamId64)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return null;

        if (_avatarCache.TryGetValue(steamId64, out var cached))
            return cached;

        try
        {
            var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={_apiKey}&steamids={steamId64}";
            var response = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);

            var players = doc.RootElement.GetProperty("response").GetProperty("players");

            foreach (var player in players.EnumerateArray())
            {
                if (player.TryGetProperty("avatarfull", out var avatar))
                {
                    var avatarUrl = avatar.GetString() ?? "";
                    _avatarCache[steamId64] = avatarUrl;
                    return avatarUrl;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DiscordUtilities] Failed to fetch avatar for {SteamId}", steamId64);
        }

        return null;
    }

    public async Task<WorkshopMapInfo?> GetWorkshopMapInfoAsync(ulong workshopId)
    {
        if (_workshopCache.TryGetValue(workshopId, out var cached))
            return cached;

        try
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("itemcount", "1"),
                new KeyValuePair<string, string>("publishedfileids[0]", workshopId.ToString())
            });

            var response = await _httpClient.PostAsync(
                "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", content);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var details = doc.RootElement.GetProperty("response").GetProperty("publishedfiledetails");

            foreach (var item in details.EnumerateArray())
            {
                var info = new WorkshopMapInfo
                {
                    WorkshopId = workshopId,
                    Title = item.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                    PreviewUrl = item.TryGetProperty("preview_url", out var preview) ? preview.GetString() ?? "" : "",
                    Description = item.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : ""
                };

                _workshopCache[workshopId] = info;
                return info;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DiscordUtilities] Failed to fetch workshop info for {WorkshopId}", workshopId);
        }

        _workshopCache[workshopId] = null;
        return null;
    }

    public static (bool IsWorkshop, ulong WorkshopId) TryParseWorkshopId(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return (false, 0);

        var parts = mapName.Replace('\\', '/').Split('/');

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("workshop", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < parts.Length && ulong.TryParse(parts[i + 1], out var workshopId))
            {
                return (true, workshopId);
            }
        }

        return (false, 0);
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed class WorkshopMapInfo
{
    public ulong WorkshopId { get; set; }
    public string Title { get; set; } = "";
    public string PreviewUrl { get; set; } = "";
    public string Description { get; set; } = "";
    public string WorkshopUrl => $"https://steamcommunity.com/sharedfiles/filedetails/?id={WorkshopId}";
}
