using System.Collections.Concurrent;
using DiscordUtilities.Services;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Misc;

namespace DiscordUtilities;

public partial class DiscordUtilities
{
    private bool _commandBlockerHooked;
    private readonly ConcurrentDictionary<ulong, (bool HasRole, long CheckedAt)> _roleChecks = new();
    private readonly ConcurrentDictionary<ulong, byte> _roleChecksInFlight = new();
    private const long RoleCacheMs = 30000;

    private bool HasVerifiedDiscordRole(ulong steamId)
    {
        var settings = Config.Verification;
        if (string.IsNullOrWhiteSpace(settings.VerifiedRoleId)) return true;
        var now = Environment.TickCount64;
        if (_roleChecks.TryGetValue(steamId, out var cached) && now - cached.CheckedAt < RoleCacheMs)
            return cached.HasRole;
        if (_roleChecksInFlight.TryAdd(steamId, 0))
        {
            var discordId = GetDiscordId(steamId);
            var token = settings.BotToken;
            var guild = settings.GuildId;
            var role = settings.VerifiedRoleId;
            _ = Task.Run(async () =>
            {
                try
                {
                    var valid = discordId != null && await DiscordBotService.HasGuildMemberRoleAsync(token, guild, discordId, role, Core.Logger);
                    _roleChecks[steamId] = (valid, Environment.TickCount64);
                }
                finally { _roleChecksInFlight.TryRemove(steamId, out _); }
            });
        }
        return false;
    }
    private readonly ConcurrentDictionary<ulong, long> _blockerLastNotice = new();
    private HashSet<string> _blockerCommands = new(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeBlockedCommand(string command)
    {
        var normalized = (command ?? string.Empty).Trim().TrimStart('!', '/');
        var space = normalized.IndexOfAny(new[] { ' ', '\t' });
        return space >= 0 ? normalized[..space] : normalized;
    }

    internal void InitializeCommandBlocker()
    {
        if (!Config.CommandBlocker.Enabled || _commandBlockerHooked) return;
        _blockerCommands = new HashSet<string>(
            (Config.CommandBlocker.BlockedCommands ?? new List<string>())
                .Select(NormalizeBlockedCommand).Where(x => x.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        if (_blockerCommands.Count == 0) return;
        _commandBlockerHooked = true;

        // CS2_Admin approach: intercept both chat commands (!ws / /ws)
        // and console commands (ws, say "!ws", say_team "!ws").
        Core.Command.HookClientChat((playerId, text, teamOnly) =>
        {
            if (string.IsNullOrWhiteSpace(text) || (text[0] != '!' && text[0] != '/'))
                return HookResult.Continue;
            return ShouldBlockDiscordCommand(playerId, text[1..]);
        });
        Core.Command.HookClientCommand((playerId, commandLine) =>
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return HookResult.Continue;
            var trimmed = commandLine.Trim();
            var space = trimmed.IndexOfAny(new[] { ' ', '\t' });
            var first = space < 0 ? trimmed : trimmed[..space];
            if (first.Equals("say", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("say_team", StringComparison.OrdinalIgnoreCase))
            {
                if (space < 0) return HookResult.Continue;
                var chat = trimmed[(space + 1)..].Trim().Trim('"');
                if (chat.Length == 0 || (chat[0] != '!' && chat[0] != '/')) return HookResult.Continue;
                return ShouldBlockDiscordCommand(playerId, chat[1..]);
            }
            return ShouldBlockDiscordCommand(playerId, first);
        });
    }

    private HookResult ShouldBlockDiscordCommand(int playerId, string rawCommand)
    {
        var command = NormalizeBlockedCommand(rawCommand);
        if (!Config.CommandBlocker.Enabled || !_blockerCommands.Contains(command))
            return HookResult.Continue;
        var player = Core.PlayerManager.GetPlayer(playerId);
        if (player == null || !player.IsValid || player.IsFakeClient) return HookResult.Continue;
        if (Config.Verification.Enabled && IsPlayerLinked(player.SteamID) && HasVerifiedDiscordRole(player.SteamID)) return HookResult.Continue;

        // No permission bypass. O(1) in-memory link and blocked-command lookups.
        // Rate-limit notices because the same chat command may arrive through both hooks.
        var now = Environment.TickCount64;
        if (!_blockerLastNotice.TryGetValue(player.SteamID, out var last) || now - last >= 2000)
        {
            _blockerLastNotice[player.SteamID] = now;
            player.SendChat(LocalizedChat("commandblocker.link_required"));
            if (Config.Debug)
                Core.Logger.LogInformation("[DiscordUtilities] Blocked '{Command}' for {SteamId}", command, player.SteamID);
        }
        return HookResult.Stop;
    }
}
