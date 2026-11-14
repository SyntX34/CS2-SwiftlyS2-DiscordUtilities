using Microsoft.Extensions.Logging;
using DiscordUtilities.Services;

namespace DiscordUtilities;

public partial class DiscordUtilities
{
    private DiscordBotService? _rconBot;

    internal void InitializeDiscordRcon()
    {
        if (!Config.DiscordRcon.Enabled)
        {
            _rconBot?.Stop();
            return;
        }

        var token = !string.IsNullOrWhiteSpace(Config.DiscordRcon.BotToken)
            ? Config.DiscordRcon.BotToken
            : Config.DiscordToServer.BotToken;

        if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(Config.DiscordRcon.ChannelId))
        {
            _rconBot?.Dispose();
            _rconBot = new DiscordBotService(
                Core.Logger,
                (_, _) => { },
                () => Config.Debug,
                (channelId, authorId, authorName, content) => OnDiscordRconMessage(token, channelId, authorId, authorName, content)
            );
            _rconBot.Start(token, Config.DiscordRcon.ChannelId);
        }
        else
        {
            _rconBot?.Stop();
        }
    }

    private void OnDiscordRconMessage(string token, string channelId, string authorId, string authorName, string content)
    {
        if (!Config.DiscordRcon.Enabled) return;

        var prefix = string.IsNullOrEmpty(Config.DiscordRcon.CommandPrefix) ? "!rcon " : Config.DiscordRcon.CommandPrefix;
        if (!content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return;

        if (Config.DiscordRcon.AllowedDiscordUserIds.Count == 0 ||
            !Config.DiscordRcon.AllowedDiscordUserIds.Contains(authorId))
        {
            _ = DiscordBotService.SendChannelMessageAsync(token, channelId, $"<@{authorId}> You are not authorized to execute server commands.", Core.Logger);
            return;
        }

        var command = content[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(command))
            return;

        Core.Scheduler.NextTick(() =>
        {
            try
            {
                Core.Engine.ExecuteCommand(command);
                Core.Logger.LogInformation("[DiscordUtilities] [DiscordRcon] {Author} ({Id}) executed: {Command}", authorName, authorId, command);
                _ = DiscordBotService.SendChannelMessageAsync(token, channelId, $"Executed: `{command}`", Core.Logger);
            }
            catch (Exception ex)
            {
                Core.Logger.LogError(ex, "[DiscordUtilities] [DiscordRcon] Failed to execute: {Command}", command);
                _ = DiscordBotService.SendChannelMessageAsync(token, channelId, $"Failed to execute `{command}`: {ex.Message}", Core.Logger);
            }
        });
    }
}
