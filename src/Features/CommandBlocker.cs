using System.Collections.Concurrent;
using DiscordUtilities.Services;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Events;

namespace DiscordUtilities;

public partial class DiscordUtilities
{
    private bool _commandBlockerHooked;
    // Cache is scoped to a connection, not a timer. Generation prevents a late
    // HTTP response from an old connection granting access to a new one.
    private readonly object _roleLock = new();
    private readonly Dictionary<ulong, bool> _roleChecks = new();
    private readonly Dictionary<ulong, bool> _linkChecks = new();
    private readonly HashSet<ulong> _roleChecksInFlight = new();
    private readonly Dictionary<ulong, long> _roleGenerations = new();
    private readonly Dictionary<int, ulong> _roleSlots = new();
    private readonly HashSet<ulong> _roleStarted = new();
    private readonly Dictionary<ulong, int> _waitingPlayers = new();
    private readonly HashSet<ulong> _pendingNoticeSent = new();

    private void InvalidateRole(ulong steamId)
    {
        // Called under _roleLock.
        _roleGenerations.TryGetValue(steamId, out var old);
        _roleGenerations[steamId] = old + 1;
        _roleChecks.Remove(steamId);
        _linkChecks.Remove(steamId);
        _roleChecksInFlight.Remove(steamId);
        _roleStarted.Remove(steamId);
        _waitingPlayers.Remove(steamId);
        _pendingNoticeSent.Remove(steamId);
        _blockerLastNotice.TryRemove(steamId, out _);
    }

    // Called only after the link database mutation succeeds. No SQL or link
    // dictionary lookup is needed in the protected-command hot path.
    private void OnCommandBlockerLinkChanged(ulong steamId, bool linked)
    {
        lock (_roleLock)
        {
            InvalidateRole(steamId);
            if (!linked)
            {
                _linkChecks[steamId] = false;
                _roleChecks[steamId] = false;
                _roleStarted.Add(steamId);
            }
        }
        // Relinking requires a new Discord role check, not an automatic grant.
        if (linked && Config.CommandBlocker.Enabled && Config.Verification.Enabled)
            _ = HasVerifiedDiscordRole(steamId);
    }

    private bool IsRolePending(ulong steamId)
    {
        lock (_roleLock) return _roleChecksInFlight.Contains(steamId);
    }

    // Only the first call per connection may start the HTTP request.
    // Subsequent commands only inspect the cached or in-flight state.
    private bool HasVerifiedDiscordRole(ulong steamId)
    {
        var settings = Config.Verification;
        // Missing configuration must never grant access.
        if (string.IsNullOrWhiteSpace(settings.VerifiedRoleId)) return false;
        long generation;
        lock (_roleLock)
        {
            if (_roleChecks.TryGetValue(steamId, out var result)) return result;
            if (!_roleStarted.Add(steamId)) return false;
            _roleChecksInFlight.Add(steamId);
            // Register generation 0 on the first connection. Without this entry,
            // the async completion discards its result and pending never clears.
            if (!_roleGenerations.TryGetValue(steamId, out generation))
            {
                generation = 0;
                _roleGenerations[steamId] = generation;
            }
        }

        var token = !string.IsNullOrWhiteSpace(settings.BotToken)
            ? settings.BotToken : Config.DiscordToServer.BotToken;
        var guild = settings.GuildId;
        var role = settings.VerifiedRoleId;

        // One background attempt per connection. Commands only read the pending
        // state or the final cache entry; they never start repeated HTTP checks.
        _ = Task.Run(async () =>
        {
            bool valid = false;
            try
            {
                // The link database may not be ready at PutInServer. Give it a
                // short grace period without launching a check per chat command.
                string? discordId = null;
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    discordId = GetDiscordId(steamId);
                    if (!string.IsNullOrWhiteSpace(discordId)) break;
                    await Task.Delay(500);
                }

                if (string.IsNullOrWhiteSpace(discordId))
                    Core.Logger.LogWarning("[DiscordUtilities] CommandBlocker: no Discord link found for {SteamId}", steamId);
                else if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(guild))
                    Core.Logger.LogWarning("[DiscordUtilities] CommandBlocker: missing bot token or guild ID for {SteamId}", steamId);
                else
                {
                    var check = DiscordBotService.HasGuildMemberRoleAsync(token, guild, discordId, role, Core.Logger);
                    var completed = await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(10)));
                    if (completed == check)
                    {
                        valid = await check;
                        // A newly restored Discord role may not be visible on the first
                        // member lookup. Confirm a negative result once before caching it.
                        if (!valid)
                        {
                            await Task.Delay(1500);
                            var retry = DiscordBotService.HasGuildMemberRoleAsync(token, guild, discordId, role, Core.Logger);
                            var retryCompleted = await Task.WhenAny(retry, Task.Delay(TimeSpan.FromSeconds(10)));
                            if (retryCompleted == retry)
                                valid = await retry;
                            else
                                Core.Logger.LogWarning("[DiscordUtilities] CommandBlocker retry timed out for {SteamId}", steamId);
                        }
                    }
                    else
                        Core.Logger.LogWarning("[DiscordUtilities] CommandBlocker role check timed out for {SteamId}", steamId);
                }
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "[DiscordUtilities] CommandBlocker role check failed for {SteamId}", steamId);
            }
            finally
            {
                int waitingPlayer = -1;
                bool notify = false;
                lock (_roleLock)
                {
                    if (_roleGenerations.TryGetValue(steamId, out var current) && current == generation)
                    {
                        // The in-memory link may have been removed while Discord replied.
                        // Never cache a grant for an unlinked Steam account.
                        var linked = IsPlayerLinked(steamId);
                        _linkChecks[steamId] = linked;
                        valid = valid && linked;
                        _roleChecks[steamId] = valid;
                        _roleChecksInFlight.Remove(steamId);
                        if (_waitingPlayers.Remove(steamId, out var id))
                        {
                            waitingPlayer = id;
                            notify = true;
                        }
                    }
                }
                // Player and chat operations must happen on the game thread.
                if (notify)
                {
                    Core.Scheduler.NextTick(() =>
                    {
                        var player = Core.PlayerManager.GetPlayer(waitingPlayer);
                        if (player == null || !player.IsValid || player.SteamID != steamId) return;
                        lock (_roleLock)
                        {
                            if (!_roleGenerations.TryGetValue(steamId, out var current) || current != generation) return;
                        }
                        if (!valid)
                            player.SendChat(LocalizedChat(GetDiscordId(steamId) == null
                                ? "commandblocker.link_required" : "commandblocker.role_missing"));
                    });
                }
            }
        });
        return false;
    }

    private void OnBlockerJoined(IOnClientPutInServerEvent e)
    {
        var player = Core.PlayerManager.GetPlayer(e.PlayerId);
        if (player == null || !player.IsValid || player.IsFakeClient || player.SteamID == 0) return;
        lock (_roleLock)
        {
            // A reused player slot can have the same SteamID as the previous
            // connection. Never trust its previous role result on a new join.
            if (_roleSlots.TryGetValue(e.PlayerId, out var previous) && previous != player.SteamID)
                InvalidateRole(previous);
            InvalidateRole(player.SteamID);
            _roleSlots[e.PlayerId] = player.SteamID;
        }
        if (Config.Verification.Enabled)
            _ = HasVerifiedDiscordRole(player.SteamID);
    }

    private void OnBlockerDisconnected(IOnClientDisconnectedEvent e)
    {
        // Use the slot map, not the player object: the latter may already be
        // unavailable by the time the disconnect event is dispatched.
        lock (_roleLock)
        {
            if (_roleSlots.Remove(e.PlayerId, out var steamId))
            {
                InvalidateRole(steamId);
            }
        }
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
        // A map change can keep the same player slot and SteamID without a
        // disconnect event. Invalidate every old role decision at map load.
        // Increment generations so responses from the previous map are ignored.
        Core.Event.OnMapLoad += (@event) =>
        {
            lock (_roleLock)
            {
                var steamIds = _roleGenerations.Keys
                    .Concat(_roleChecks.Keys)
                    .Concat(_roleSlots.Values)
                    .Distinct().ToArray();
                foreach (var steamId in steamIds)
                    InvalidateRole(steamId);
                _roleSlots.Clear();
            }
        };
        Core.Event.OnClientPutInServer += OnBlockerJoined;
        Core.Event.OnClientDisconnected += OnBlockerDisconnected;

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

    // Send chat on the next game tick so stopping the original command
    // cannot suppress the feedback message.
    private void SendBlockerMessage(int playerId, ulong steamId, string key)
    {
        var message = LocalizedChat(key);
        Core.Scheduler.NextTick(() =>
        {
            var recipient = Core.PlayerManager.GetPlayer(playerId);
            if (recipient != null && recipient.IsValid && recipient.SteamID == steamId)
                recipient.SendChat(message);
        });
    }

    private HookResult ShouldBlockDiscordCommand(int playerId, string rawCommand)
    {
        var command = NormalizeBlockedCommand(rawCommand);
        if (!Config.CommandBlocker.Enabled || !_blockerCommands.Contains(command))
            return HookResult.Continue;
        var player = Core.PlayerManager.GetPlayer(playerId);
        if (player == null || !player.IsValid || player.IsFakeClient) return HookResult.Continue;
        // Fail closed if the join event has not yet registered this exact
        // player/SteamID pair (e.g. during a map transition). Never reuse a
        // cached True from a previous connection in this case.
        bool freshConnection = false;
        lock (_roleLock)
        {
            if (!_roleSlots.TryGetValue(playerId, out var trackedSteamId) || trackedSteamId != player.SteamID)
            {
                if (trackedSteamId != 0 && trackedSteamId != player.SteamID)
                    InvalidateRole(trackedSteamId);
                InvalidateRole(player.SteamID);
                _roleSlots[playerId] = player.SteamID;
                freshConnection = true;
            }
        }
        if (freshConnection && Config.Verification.Enabled)
            _ = HasVerifiedDiscordRole(player.SteamID);
        // All checks below are in-memory cache reads. The link cache is
        // updated on join/role-check completion and by link/unlink events.
        bool linked;
        lock (_roleLock)
            linked = _linkChecks.TryGetValue(player.SteamID, out var cachedLink) && cachedLink;
        if (Config.Verification.Enabled && !linked)
        {
            // While the initial check is pending, do not incorrectly say unlinked.
            if (!IsRolePending(player.SteamID))
            {
                var nowUnlinked = Environment.TickCount64;
                if (!_blockerLastNotice.TryGetValue(player.SteamID, out var lastUnlinked) || nowUnlinked - lastUnlinked >= 2000)
                {
                    _blockerLastNotice[player.SteamID] = nowUnlinked;
                    SendBlockerMessage(playerId, player.SteamID, "commandblocker.link_required");
                }
                return HookResult.Stop;
            }
        }
        if (Config.Verification.Enabled && HasVerifiedDiscordRole(player.SteamID))
        {
            return HookResult.Continue;
        }

        var pending = Config.Verification.Enabled && IsRolePending(player.SteamID);
        if (pending)
        {
            bool sendPending;
            lock (_roleLock)
            {
                _waitingPlayers[player.SteamID] = playerId;
                sendPending = _pendingNoticeSent.Add(player.SteamID);
            }
            // This message is emitted once per connection, not once per command.
            if (sendPending)
                SendBlockerMessage(playerId, player.SteamID, "commandblocker.role_check_pending");
            return HookResult.Stop;
        }

        // The check is finished. Show the actual reason when a blocked
        // command is attempted, with a short duplicate-hook rate limit.
        var now = Environment.TickCount64;
        if (!_blockerLastNotice.TryGetValue(player.SteamID, out var last) || now - last >= 2000)
        {
            _blockerLastNotice[player.SteamID] = now;
            SendBlockerMessage(playerId, player.SteamID, GetDiscordId(player.SteamID) != null
                ? "commandblocker.role_missing" : "commandblocker.link_required");
        }
        return HookResult.Stop;
    }
}
