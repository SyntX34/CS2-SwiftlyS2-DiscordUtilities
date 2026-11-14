using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Misc;
using DiscordUtilities.Services;

namespace DiscordUtilities;

public partial class DiscordUtilities
{
    private bool _commandBlockerHooked;

    internal void InitializeCommandBlocker()
    {
        if (!Config.CommandBlocker.Enabled || _commandBlockerHooked)
            return;

        _commandBlockerHooked = true;

        Core.Command.HookClientCommand((playerId, commandLine) =>
        {
            if (!Config.CommandBlocker.Enabled || Config.CommandBlocker.BlockedCommands.Count == 0)
                return HookResult.Continue;

            var trimmed = commandLine.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return HookResult.Continue;

            var spaceIdx = trimmed.IndexOf(' ');
            var cmd = spaceIdx > 0 ? trimmed[..spaceIdx] : trimmed;

            var isBlocked = false;
            foreach (var blocked in Config.CommandBlocker.BlockedCommands)
            {
                if (string.Equals(cmd, blocked, StringComparison.OrdinalIgnoreCase))
                {
                    isBlocked = true;
                    break;
                }
            }

            if (!isBlocked)
                return HookResult.Continue;

            var player = Core.PlayerManager.GetPlayer(playerId);
            if (player == null || !player.IsValid || player.IsFakeClient)
                return HookResult.Continue;

            if (!string.IsNullOrWhiteSpace(Config.CommandBlocker.BypassPermission) &&
                Core.Permission.PlayerHasPermission(player.SteamID, Config.CommandBlocker.BypassPermission))
            {
                return HookResult.Continue;
            }

            if (!string.IsNullOrWhiteSpace(Config.CommandBlocker.BlockedMessage))
                player.SendChat(Config.CommandBlocker.BlockedMessage);

            if (!string.IsNullOrWhiteSpace(Config.CommandBlocker.WebhookUrl))
            {
                var name = player.Controller?.PlayerName ?? "Unknown";
                var steamId = player.SteamID.ToString();
                _ = Webhook.SendEmbedAsync(
                    Config.CommandBlocker.WebhookUrl,
                    new DiscordEmbed
                    {
                        Title = "Blocked Command Attempt",
                        Description = $"**Player:** {name} (`{steamId}`)\n**Command:** `{trimmed}`",
                        Color = WebhookService.ParseColor(Config.CommandBlocker.EmbedColor)
                    }
                );
            }

            if (Config.Debug)
                Core.Logger.LogInformation("[DiscordUtilities] Blocked command '{Cmd}' from {Name} ({SteamId})", cmd, player.Controller?.PlayerName ?? "Unknown", player.SteamID);

            return HookResult.Stop;
        });
    }
}
