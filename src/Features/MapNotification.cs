using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.GameEvents;
using DiscordUtilities.Services;

namespace DiscordUtilities;

public partial class DiscordUtilities
{
    private string _currentMapName = "";
    private string _lastNotifiedMap = "";
    private string? _mapStatusMessageId;
    private CancellationTokenSource? _mapRefreshCts;
    private readonly SemaphoreSlim _mapUpdateLock = new(1, 1);
    private int _cachedPlayerCount;
    private string _cachedServerName = "CS2 Server";
    private string _cachedConnectAddress = "";
    private int _mapRefreshInProgress;

    internal void InitializeMapNotification()
    {
        if (!Config.MapNotification.Enabled) return;

        if (string.IsNullOrWhiteSpace(Config.MapNotification.WebhookUrl))
        {
            Core.Logger.LogWarning("[DiscordUtilities] MapNotification enabled but no webhook URL set");
            return;
        }

        Core.Scheduler.NextTick(() =>
        {
            _cachedPlayerCount = GetOnlinePlayerCount();
            _cachedServerName = GetServerDisplayName();
            _cachedConnectAddress = GetServerConnectAddress();
            try
            {
                var map = Core.ConVar.FindAsString("mapname")?.ValueAsString;
                if (!string.IsNullOrWhiteSpace(map)) _currentMapName = map;
            }
            catch { }
        });

        Core.Event.OnMapLoad += (@event) =>
        {
            if (!string.IsNullOrWhiteSpace(@event.MapName))
            {
                var newMap = @event.MapName;
                var isDifferentMap = !string.Equals(_lastNotifiedMap, newMap, StringComparison.OrdinalIgnoreCase);

                _currentMapName = newMap;
                _cachedPlayerCount = GetOnlinePlayerCount();
                _cachedServerName = GetServerDisplayName();
                _cachedConnectAddress = GetServerConnectAddress();

                if (isDifferentMap)
                {
                    _ = Task.Run(() => OnMapLoadedAsync(isExtension: false));
                }
            }
        };

        // Hook map extension commands / chat
        Core.Command.HookClientChat((playerId, text, teamonly) =>
        {
            if (string.IsNullOrWhiteSpace(text)) return HookResult.Continue;
            var trimmed = text.Trim().ToLowerInvariant();
            if (trimmed is "!extend" or "/extend" or "!ext" or "/ext" or "!mapextend" or "/mapextend")
            {
                _ = Task.Run(() => OnMapLoadedAsync(isExtension: true));
            }
            return HookResult.Continue;
        });

        try { var path = Path.Combine(Core.PluginDataDirectory, "map_status_message_id.txt");
            if (File.Exists(path)) _mapStatusMessageId = File.ReadAllText(path).Trim(); } catch { }
        _mapRefreshCts?.Cancel();
        _mapRefreshCts = new CancellationTokenSource();
        _ = MapRefreshLoopAsync(_mapRefreshCts.Token);
        Core.Logger.LogInformation("[DiscordUtilities] MapNotification registered");
    }

    private async Task MapRefreshLoopAsync(CancellationToken ct)
    {
        // Based on CS2_Admin DiscordServerStatusService: update one saved message
        // independently of player presence; game-state snapshots stay on game thread.
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(Math.Max(10, Config.MapNotification.RefreshSeconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!Config.MapNotification.Enabled) continue;
                Core.Scheduler.NextTick(() =>
                {
                    _cachedPlayerCount = GetOnlinePlayerCount();
                    _cachedServerName = GetServerDisplayName();
                    _cachedConnectAddress = GetServerConnectAddress();
                });
                if (Interlocked.Exchange(ref _mapRefreshInProgress, 1) != 0) continue;
                try
                {
                    await OnMapLoadedAsync(forceRefresh: true);
                    if (Config.Debug)
                        Core.Logger.LogInformation("[DiscordUtilities] MapNotification periodic status update finished");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Core.Logger.LogWarning(ex, "[DiscordUtilities] MapNotification periodic status update failed");
                }
                finally { Interlocked.Exchange(ref _mapRefreshInProgress, 0); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[DiscordUtilities] MapNotification timer stopped unexpectedly");
        }
    }

    private string GetCurrentMapDisplayName()
    {
        // Map load events provide the exact path (and often Workshop ID).
        // The convar is a fallback for servers where no map-load event was seen yet.
        var value = _currentMapName;
        // Initial map and map-load event populate _currentMapName on game thread.
        // Never query CS2 convars from the background Discord timer.
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            value = _lastNotifiedMap;
        return string.IsNullOrWhiteSpace(value) ? "Loading..." : value.Trim();
    }

    private static string CleanMapName(string mapName)
    {
        var clean = mapName.Replace('\\', '/').TrimEnd('/');
        var index = clean.LastIndexOf('/');
        return index >= 0 ? clean[(index + 1)..] : clean;
    }

    private ulong ResolveWorkshopId(string fullMapName, string cleanMapName)
    {
        var (_, parsedId) = SteamApiService.TryParseWorkshopId(fullMapName);
        if (parsedId != 0) return parsedId;
        return 0;
    }

    private async Task OnMapLoadedAsync(bool isExtension = false, bool forceRefresh = false)
    {
        try
        {
            var config = Config.MapNotification;

            if (!forceRefresh && Webhook.IsOnCooldown("map_notification", config.CooldownSeconds))
                return;

            var mapName = GetCurrentMapDisplayName();
            if (mapName == "Loading...") return;
            if (!forceRefresh && !isExtension && string.Equals(_lastNotifiedMap, mapName, StringComparison.OrdinalIgnoreCase))
                return;
            var cleanMapName = CleanMapName(mapName);
            var workshopId = ResolveWorkshopId(mapName, cleanMapName);
            var isWorkshop = workshopId != 0;

            // The timer may run while the game server is hibernating.
            // Never enumerate game entities from the background thread.
            var playerCount = Volatile.Read(ref _cachedPlayerCount);
            var serverName = _cachedServerName;
            var connectAddress = _cachedConnectAddress;

            var titlePrefix = isExtension ? DiscordText.Get("discord.map.extended", "⏳ Map Extended") : DiscordText.Get("discord.map.title", "🗺️ Map Notification");

            var embed = new DiscordEmbed
            {
                Title = titlePrefix,
                Color = WebhookService.ParseColor(config.EmbedColor),
                Footer = new EmbedFooter { Text = $"{serverName} • Discord Utilities" }
            };
            embed.WithTimestamp();

            string? mapImageUrl = null;
            WorkshopMapInfo? workshopInfo = null;

            if (isWorkshop)
            {
                workshopInfo = await SteamApi.GetWorkshopMapInfoAsync(workshopId);
            }

            var displayMapName = !string.IsNullOrWhiteSpace(workshopInfo?.Title)
                ? workshopInfo.Title : cleanMapName;
            embed.AddField(DiscordText.Get("discord.map.current", "📍 Current Map"), $"`{displayMapName}`", true);
            if (isWorkshop && config.ShowWorkshopId)
            {
                var workshopUrl = workshopInfo?.WorkshopUrl ?? $"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopId}";
                embed.Url = workshopUrl;
                embed.AddField(DiscordText.Get("discord.map.workshop_id", "Workshop ID"), $"[{workshopId}]({workshopUrl})", true);
            }
            mapImageUrl = await SteamApi.GetMapImageAsync(cleanMapName, isWorkshop ? workshopId : null);
            // If no map preview is available, show the configured community banner as the main image.

            if (config.ShowPlayerCount)
                embed.AddField(DiscordText.Get("discord.map.players", "👥 Players Online"), $"{playerCount}", true);

            var isHttpConnect = !string.IsNullOrWhiteSpace(connectAddress) &&
                                (connectAddress.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                 connectAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

            if (config.ShowServerIP && !string.IsNullOrWhiteSpace(connectAddress))
            {
                if (isHttpConnect)
                {
                    embed.AddField(DiscordText.Get("discord.map.quick_connect", "🔗 Quick Connect"), $"[👉 Click Here to Connect]({connectAddress})", false);
                }
                else
                {
                    embed.AddField(DiscordText.Get("discord.map.quick_connect", "🔗 Quick Connect"), $"`connect {connectAddress}`", false);
                }
            }

            // Thumbnail (Server / Community Banner logo)
            if (!string.IsNullOrWhiteSpace(config.BannerUrl))
            {
                embed.Thumbnail = new EmbedImage { Url = config.BannerUrl };
            }

            // Main Image (Map Image)
            var mainImageUrl = !string.IsNullOrWhiteSpace(mapImageUrl) ? mapImageUrl : config.BannerUrl;
            if (!string.IsNullOrWhiteSpace(mainImageUrl))
            {
                embed.Image = new EmbedImage { Url = mainImageUrl };
            }

            // Action Row Buttons (Connect Now & Steam Workshop Link)
            List<DiscordComponentActionRow>? components = null;
            var buttons = new List<DiscordComponentButton>();

            if (!string.IsNullOrWhiteSpace(connectAddress))
            {
                string buttonUrl;

                if (isHttpConnect)
                {
                    buttonUrl = connectAddress;
                }
                else
                {
                    var target = connectAddress.Replace("connect ", "", StringComparison.OrdinalIgnoreCase).Trim();
                    buttonUrl = $"https://vauff.com/connect.php?ip={target}";
                }

                buttons.Add(new DiscordComponentButton
                {
                    Label = DiscordText.Get("discord.map.connect_now", "Connect Now"),
                    Url = buttonUrl,
                    Emoji = new DiscordEmoji { Name = "🎮" }
                });
            }

            if (isWorkshop && workshopId > 0 && config.ShowWorkshopId)
            {
                var wsUrl = workshopInfo?.WorkshopUrl ?? $"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopId}";
                buttons.Add(new DiscordComponentButton
                {
                    Label = DiscordText.Get("discord.map.workshop_page", "Workshop Page"),
                    Url = wsUrl,
                    Emoji = new DiscordEmoji { Name = "🗺️" }
                });
            }

            if (buttons.Count > 0)
            {
                components =
                [
                    new DiscordComponentActionRow
                    {
                        Components = buttons
                    }
                ];
            }

            await _mapUpdateLock.WaitAsync();
            try
            {
                var id = await Webhook.UpsertEmbedAsync(config.WebhookUrl, embed, _mapStatusMessageId, username: serverName, components: components);
                if (id == null && _mapStatusMessageId != null)
                    Core.Logger.LogWarning("[DiscordUtilities] Could not update or recreate map status message {MessageId}; keeping saved ID until a successful update", _mapStatusMessageId);
                if (id != null)
                {
                    _lastNotifiedMap = mapName;
                    _mapStatusMessageId = id;
                    try { Directory.CreateDirectory(Core.PluginDataDirectory);
                        File.WriteAllText(Path.Combine(Core.PluginDataDirectory, "map_status_message_id.txt"), id); } catch { }
                }
            }
            finally { _mapUpdateLock.Release(); }
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[DiscordUtilities] MapNotification failed");
        }
    }

    private int GetOnlinePlayerCount()
    {
        try
        {
            return Core.PlayerManager.GetAllValidPlayers().Count(player => player.IsValid && !player.IsFakeClient);
        }
        catch
        {
            return 0;
        }
    }
}
