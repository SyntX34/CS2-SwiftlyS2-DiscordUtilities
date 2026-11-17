using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using DiscordUtilities.Services;

namespace DiscordUtilities;

public partial class DiscordUtilities
{
    private static readonly HashSet<string> TrackedAdminCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        // player punishment
        "ban", "unban", "banip",
        "kick",
        "mute", "unmute",
        "gag", "ungag",
        "silence", "unsilence",
        "slay", "slap",
        "noclip", "god", "teleport",
        "freeze", "unfreeze",
        "respawn", "burn",
        "blind", "beacon",
        "hp", "armor", "speed",
        "gravity", "team",
        "give", "strip", "spec",
        // admin management
        "addadmin", "removeadmin", "reloadadmins",
        "reloadplugins", "reloadplugin",
        "plugins", "rcon",
        "cvar", "exec",
        "csay", "hsay", "psay", "msay", "tsay",
        "vote", "votekick", "voteban", "votemap",
        "admin", "menu",
        // server / map
        "changelevel", "map", "host_workshop_map",
        "mp_restartgame", "mp_warmup_start", "mp_warmup_end"
    };

    internal void InitializeAdminLogs()
    {
        if (!Config.AdminLogs.Enabled) return;

        if (string.IsNullOrWhiteSpace(Config.AdminLogs.WebhookUrl))
        {
            Core.Logger.LogWarning("[DiscordUtilities] AdminLogs enabled but no webhook URL set");
            return;
        }

        // Hook console commands typed by clients
        Core.Command.HookClientCommand((playerId, commandLine) =>
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return HookResult.Continue;

            var parts = commandLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return HookResult.Continue;

            var rawCmd = parts[0];
            var args = parts.Length > 1 ? parts[1] : "";

            if (TryResolveAdminCommand(rawCmd, out var normalizedCmd))
            {
                var player = Core.PlayerManager.GetPlayer(playerId);
                _ = Task.Run(() => OnAdminCommandExecutedAsync(normalizedCmd, args, player));
            }

            return HookResult.Continue;
        });

        // Hook chat commands (!slap, /ban, etc.)
        Core.Command.HookClientChat((playerId, text, teamonly) =>
        {
            if (string.IsNullOrWhiteSpace(text))
                return HookResult.Continue;

            text = text.Trim();
            if (text.StartsWith('!') || text.StartsWith('/'))
            {
                var withoutPrefix = text[1..];
                var parts = withoutPrefix.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    var rawCmd = parts[0];
                    var args = parts.Length > 1 ? parts[1] : "";

                    if (TryResolveAdminCommand(rawCmd, out var normalizedCmd))
                    {
                        var player = Core.PlayerManager.GetPlayer(playerId);
                        _ = Task.Run(() => OnAdminCommandExecutedAsync(normalizedCmd, args, player));
                    }
                }
            }

            return HookResult.Continue;
        });

        // Hook server console commands
        Core.Event.OnCommandExecuteHook += (@event) =>
        {
            if (Config.AdminLogs.IgnoreConsole)
                return;

            var rawCmd = @event.Command.Arg(0);
            if (string.IsNullOrEmpty(rawCmd)) return;

            if (TryResolveAdminCommand(rawCmd, out var normalizedCmd))
            {
                var argS = @event.Command.ArgS ?? "";
                _ = Task.Run(() => OnAdminCommandExecutedAsync(normalizedCmd, argS, null));
            }
        };

        Core.Logger.LogInformation("[DiscordUtilities] AdminLogs registered for both chat and console commands");
    }

    private bool TryResolveAdminCommand(string inputCommand, out string normalizedCommand)
    {
        normalizedCommand = inputCommand.TrimStart('!', '/');

        // Ignore standard in-game chat commands
        if (normalizedCommand.Equals("say", StringComparison.OrdinalIgnoreCase) ||
            normalizedCommand.Equals("say_team", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Check if raw command name is tracked
        if (TrackedAdminCommands.Contains(normalizedCommand))
            return true;

        // Check stripped prefix sw_ / css_ / sm_
        var stripped = normalizedCommand;
        if (stripped.StartsWith("sw_", StringComparison.OrdinalIgnoreCase))
            stripped = stripped[3..];
        else if (stripped.StartsWith("css_", StringComparison.OrdinalIgnoreCase))
            stripped = stripped[4..];
        else if (stripped.StartsWith("sm_", StringComparison.OrdinalIgnoreCase))
            stripped = stripped[3..];

        if (TrackedAdminCommands.Contains(stripped))
        {
            normalizedCommand = stripped;
            return true;
        }

        if (Config.AdminLogs.LogCommands && (inputCommand.StartsWith("sw_", StringComparison.OrdinalIgnoreCase) ||
                                            inputCommand.StartsWith("css_", StringComparison.OrdinalIgnoreCase) ||
                                            inputCommand.StartsWith("sm_", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private async Task OnAdminCommandExecutedAsync(string commandName, string commandArgs, IPlayer? adminPlayer)
    {
        try
        {
            var config = Config.AdminLogs;

            if (adminPlayer == null && config.IgnoreConsole)
                return;

            if (Webhook.IsOnCooldown($"admin_{commandName}", config.CooldownSeconds))
                return;

            var (category, icon) = CategorizeCommand(commandName);
            var serverName = GetServerDisplayName();

            var adminName = adminPlayer != null ? adminPlayer.Name : "Console / Server";
            ulong adminSteamId = adminPlayer != null ? adminPlayer.SteamID : 0;
            string? adminAvatar = null;

            if (adminSteamId != 0)
            {
                adminAvatar = await SteamApi.GetPlayerAvatarAsync(adminSteamId);
            }

            var embed = new DiscordEmbed
            {
                Title = $"{icon} Admin Command: `{commandName}`",
                Color = WebhookService.ParseColor(config.EmbedColor),
                Author = new EmbedAuthor
                {
                    Name = adminName,
                    IconUrl = adminAvatar
                },
                Footer = new EmbedFooter { Text = $"{serverName} • Discord Utilities" }
            };
            embed.WithTimestamp();

            embed.AddField("Administrator", adminSteamId != 0 ? $"**{adminName}** (`{adminSteamId}`)" : $"**{adminName}**", true);
            embed.AddField("Category", category, true);

            if (!string.IsNullOrWhiteSpace(commandArgs))
            {
                var displayArgs = commandArgs.Length > 900 ? commandArgs[..900] + "..." : commandArgs;
                embed.AddField("Arguments", $"```{displayArgs}```", false);
            }

            if (!string.IsNullOrWhiteSpace(config.BannerUrl))
            {
                embed.Image = new EmbedImage { Url = config.BannerUrl };
            }

            await Webhook.SendEmbedAsync(config.WebhookUrl, embed, username: $"{serverName} Admin Logs");
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[DiscordUtilities] AdminLogs failed for: {Command}", commandName);
        }
    }

    private static (string Category, string Icon) CategorizeCommand(string command)
    {
        var lower = command.ToLowerInvariant();

        if (lower.Contains("ban")) return ("🔨 Ban", "🔨");
        if (lower.Contains("kick")) return ("👢 Kick", "👢");
        if (lower is "mute" or "gag" or "silence") return ("🔇 Mute/Gag", "🔇");
        if (lower is "unmute" or "ungag" or "unsilence") return ("🔊 Unmute/Ungag", "🔊");
        if (lower is "slay" or "slap" or "burn" or "blind" or "beacon") return ("⚡ Punish", "⚡");
        if (lower is "freeze" or "unfreeze" or "respawn") return ("💀 Player Action", "💀");
        if (lower is "noclip" or "god" or "teleport") return ("🛡️ Admin Tool", "🛡️");
        if (lower is "hp" or "armor" or "speed" or "gravity" or "team" or "give" or "strip" or "spec") return ("🎮 Player Modify", "🎮");
        if (lower is "csay" or "hsay" or "psay" or "msay" or "tsay") return ("💬 Admin Chat", "💬");
        if (lower.Contains("vote")) return ("🗳️ Vote", "🗳️");
        if (lower is "changelevel" or "map" or "host_workshop_map" or "votemap") return ("🗺️ Map Change", "🗺️");
        if (lower is "rcon" or "exec" or "cvar") return ("🖥️ RCON/Exec", "🖥️");
        if (lower is "addadmin" or "removeadmin" or "reloadadmins" or "admin" or "menu") return ("👑 Admin Mgmt", "👑");
        if (lower.Contains("plugin") || lower.Contains("reload")) return ("🔧 Plugin Mgmt", "🔧");
        if (lower.StartsWith("sw_") || lower.StartsWith("css_") || lower.StartsWith("sm_")) return ("⚙️ Server Command", "⚙️");
        if (lower.StartsWith("mp_")) return ("⚙️ Game Setting", "⚙️");

        return ("📋 Command", "📋");
    }
}
