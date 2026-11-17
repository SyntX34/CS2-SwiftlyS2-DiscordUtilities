using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;
using DiscordUtilities.Services;

namespace DiscordUtilities;

public partial class DiscordUtilities
{
    internal void InitializePlayerJoined()
    {
        if (!Config.PlayerJoined.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(Config.PlayerJoined.WebhookUrl))
        {
            Core.Logger.LogWarning(
                "[DiscordUtilities] PlayerJoined enabled but no webhook URL set"
            );

            return;
        }

        Core.Event.OnClientPutInServer += OnClientPutInServer;
        Core.Event.OnClientDisconnected += OnClientDisconnected;

        Core.Logger.LogInformation(
            "[DiscordUtilities] PlayerJoined registered"
        );
    }

    private void OnClientPutInServer(IOnClientPutInServerEvent @event)
    {
        try
        {
            var player = Core.PlayerManager.GetPlayer(@event.PlayerId);

            if (player == null)
            {
                Core.Logger.LogWarning(
                    "[DiscordUtilities] PlayerPutInServer: player not found for slot {PlayerId}",
                    @event.PlayerId
                );

                return;
            }

            if (player.IsFakeClient)
                return;

            var playerName = player.Name;
            var steamId = player.SteamID;
            var ip = player.IPAddress;
            _ = SendPlayerJoinedAsync(playerName, steamId, ip);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(
                ex,
                "[DiscordUtilities] Failed to process player joined event"
            );
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        try
        {
            var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
            var playerName = player?.Name ?? "Unknown";
            var steamId = player?.SteamID ?? 0;
            var connectedTime = player?.ConnectedTime ?? 0;
            var playerSlot = @event.PlayerId;
            var reason = @event.Reason.ToString();
            var ip = player?.IPAddress;

            if (player?.IsFakeClient == true)
                return;

            _ = Task.Run(() =>
                SendPlayerLeftAsync(
                    playerName,
                    steamId,
                    connectedTime,
                    playerSlot,
                    reason,
                    ip
                )
            );
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(
                ex,
                "[DiscordUtilities] Failed to process player disconnected event"
            );
        }
    }

    private async Task SendPlayerJoinedAsync(string playerName, ulong steamId, string? ip)
    {
        try
        {
            var config = Config.PlayerJoined;

            if (Webhook.IsOnCooldown(
                    $"player_join_{steamId}",
                    config.CooldownSeconds))
            {
                return;
            }

            var serverName = GetServerDisplayName();

            var country = await DiscordPlayerFormat.LookupCountryAsync(ip);
            var line = DiscordPlayerFormat.PlayerLink(playerName, steamId, country);
            var message = DiscordText.Get("discord.player.joined", "{0} joined the server.", line);
            await Webhook.SendMessageAsync(config.WebhookUrl, message, username: serverName);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(
                ex,
                "[DiscordUtilities] Failed to send player joined notification"
            );
        }
    }

    private async Task SendPlayerLeftAsync(
        string playerName,
        ulong steamId,
        uint connectedTime,
        int playerSlot,
        string disconnectReason, string? ip)
    {
        try
        {
            var config = Config.PlayerJoined;

            if (Webhook.IsOnCooldown(
                    $"player_leave_{steamId}_{playerSlot}",
                    config.CooldownSeconds))
            {
                return;
            }

            var serverName = GetServerDisplayName();

            var country = await DiscordPlayerFormat.LookupCountryAsync(ip);
            var line = DiscordPlayerFormat.PlayerLink(playerName, steamId, country);
            var message = DiscordText.Get("discord.player.left", "{0} left the server.", line);
            await Webhook.SendMessageAsync(config.WebhookUrl, message, username: serverName);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(
                ex,
                "[DiscordUtilities] Failed to send player left notification"
            );
        }
    }

}