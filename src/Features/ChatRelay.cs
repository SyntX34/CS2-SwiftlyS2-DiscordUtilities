using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Misc;
using DiscordUtilities.Services;

namespace DiscordUtilities;

public partial class DiscordUtilities
{
    private readonly ConcurrentDictionary<ulong, DateTime> _chatCooldowns = new();
    private Guid _chatHookGuid = Guid.Empty;

    internal void InitializeChatRelay()
    {
        if (!Config.ChatRelay.Enabled) return;

        if (string.IsNullOrWhiteSpace(Config.ChatRelay.WebhookUrl))
        {
            Core.Logger.LogWarning("[DiscordUtilities] ChatRelay enabled but no webhook URL set");
            return;
        }

        if (_chatHookGuid != Guid.Empty)
        {
            Core.Command.UnhookClientChat(_chatHookGuid);
            _chatHookGuid = Guid.Empty;
        }

        _chatHookGuid = Core.Command.HookClientChat((playerId, text, teamonly) =>
        {
            var player = Core.PlayerManager.GetPlayer(playerId);
            if (player == null || !player.IsValid || player.IsFakeClient) return HookResult.Continue;
            var name = player.Name;
            var steam = player.SteamID;
            var ip = player.IPAddress;
            var team = (int)(player.Controller?.TeamNum ?? 0);
            _ = OnPlayerChatAsync(name, steam, ip, text, teamonly, team);
            return HookResult.Continue;
        });

        Core.Logger.LogInformation("[DiscordUtilities] ChatRelay registered via HookClientChat");
    }

    private async Task OnPlayerChatAsync(string playerName, ulong steamId64, string? ip, string text, bool teamOnly, int team)
    {
        try
        {
            var config = Config.ChatRelay;

            if (string.IsNullOrWhiteSpace(text)) return;
            if (teamOnly && config.IgnoreTeamChat) return;

            var trimmedText = text.Trim();
            if (config.IgnoreCommands)
            {
                if (trimmedText.StartsWith('!') || trimmedText.StartsWith('/'))
                    return;

                if (config.IgnoredPrefixes != null && config.IgnoredPrefixes.Count > 0)
                {
                    foreach (var prefix in config.IgnoredPrefixes)
                    {
                        if (string.IsNullOrWhiteSpace(prefix)) continue;
                        if (trimmedText.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
                            return;
                    }
                }
            }



            if (config.CooldownSeconds > 0 && steamId64 != 0)
            {
                var now = DateTime.UtcNow;
                if (_chatCooldowns.TryGetValue(steamId64, out var lastMsg) &&
                    (now - lastMsg).TotalSeconds < config.CooldownSeconds)
                    return;

                _chatCooldowns[steamId64] = now;
            }

            string? avatarUrl = null;
            if (config.UseSteamAvatars && steamId64 != 0)
                avatarUrl = await SteamApi.GetPlayerAvatarAsync(steamId64);

            var steamId2 = ConvertSteamId64ToSteamId2(steamId64);
            var linkedName = DiscordPlayerFormat.PlayerLink(playerName, steamId64);

            var teamTag = team switch
            {
                2 => "[T]", 3 => "[CT]", 1 => "[SPEC]", _ => "[SPEC]"
            };
            var formattedMessage = $"{teamTag} {linkedName}: {text}";
            if (teamOnly) formattedMessage = $"{teamTag} {linkedName} (TEAM): {text}";
            // Webhook username is the configured bot/server identity, never a second player name.
            var webhookUsername = GetServerDisplayName();

            // Send clean Discord message (no embed banner, pure webhook format)
            await Webhook.SendMessageAsync(
                config.WebhookUrl,
                content: formattedMessage,
                username: webhookUsername,
                avatarUrl: avatarUrl);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[DiscordUtilities] ChatRelay failed");
        }
    }

    private static string ConvertSteamId64ToSteamId2(ulong steamId64)
    {
        if (steamId64 < 76561197960265728)
            return steamId64.ToString();

        var accountId = steamId64 - 76561197960265728;
        var y = accountId % 2;
        var z = accountId / 2;

        return $"STEAM_0:{y}:{z}";
    }
}
