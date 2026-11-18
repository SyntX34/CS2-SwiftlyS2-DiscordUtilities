using System.Collections.Concurrent;
using System.Text.Json;
using System.Data;
using Microsoft.Extensions.Logging;
using DiscordUtilities.Services;
using SwiftlyS2.Shared.Players;

namespace DiscordUtilities;

public partial class DiscordUtilities
{
    private DiscordBotService? _verificationBot;
    private string? _verifyPanelMessageId;
    private readonly SemaphoreSlim _verifyPanelLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingVerification> _pendingVerifications = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ulong, string> _linkedAccounts = new();
    private readonly object _linksFileLock = new();
    private bool _verificationInitialized;
    private bool _linkDatabaseReady;

    private sealed record PendingVerification(ulong SteamId, string PlayerName, DateTime ExpiresAtUtc);

    internal void InitializeVerification()
    {
        if (!Config.Verification.Enabled && !Config.CallAdmin.Enabled)
        {
            _verificationBot?.Stop();
            return;
        }

        if (Config.Verification.Enabled)
        {
            LoadLinkedAccounts();
            if (!_linkDatabaseReady) Core.Logger.LogError("[DiscordUtilities] Link database unavailable: linking is disabled until storage works. CommandBlocker will deny unlinked users.");
            Core.Logger.LogInformation("[DiscordUtilities] Link database: {Path}; linked accounts loaded: {Count}", "mysql_detailed", _linkedAccounts.Count);
        }

        if (Config.Verification.Enabled && !_verificationInitialized)
        {
            _verificationInitialized = true;

            var unlinkCmd = GetUnlinkCommand();

            foreach (var linkCmd in GetLinkCommands()) Core.Command.RegisterCommand(linkCmd, (context) =>
            {
                if (!Config.Verification.Enabled) return;
                var player = context.Sender;
                if (player == null || !player.IsValid || player.IsFakeClient) return;

                if (!_linkDatabaseReady) { player.SendChat(LocalizedChat("verification.database_error")); SendInvite(player); return; }
                if (_linkedAccounts.TryGetValue(player.SteamID, out var existingDiscordId))
                {
                    player.SendChat(LocalizedChat("verification.already_linked", existingDiscordId, unlinkCmd));
                    SendInvite(player);
                    return;
                }

                var code = GenerateVerificationCode();
                var expiryMinutes = Math.Max(1, Config.Verification.CodeExpiryMinutes);
                _pendingVerifications[code] = new PendingVerification(player.SteamID, player.Controller?.PlayerName ?? "Unknown", DateTime.UtcNow.AddMinutes(expiryMinutes));

                player.SendChat(LocalizedChat("verification.code_generated", code));
                player.SendChat(LocalizedChat("verification.code_instructions", code, expiryMinutes));
                SendInvite(player);
            });

            foreach (var commandName in GetUnlinkCommands()) Core.Command.RegisterCommand(commandName, (context) =>
            {
                if (!Config.Verification.Enabled) return;
                var player = context.Sender;
                if (player == null || !player.IsValid || player.IsFakeClient) return;

                if (!_linkDatabaseReady) { player.SendChat(LocalizedChat("verification.database_error")); SendInvite(player); return; }
                if (_linkedAccounts.TryRemove(player.SteamID, out var removedDiscordId))
                {
                    if (!SaveLinkedAccounts()) { _linkedAccounts[player.SteamID] = removedDiscordId; player.SendChat(LocalizedChat("verification.database_error")); SendInvite(player); return; }
                    OnCommandBlockerLinkChanged(player.SteamID, false);
                    // Database unlink succeeded; Discord role removal is an asynchronous best-effort operation.
                    if (!string.IsNullOrWhiteSpace(Config.Verification.GuildId) &&
                        !string.IsNullOrWhiteSpace(Config.Verification.VerifiedRoleId))
                    {
                        var guildId = Config.Verification.GuildId;
                        var roleId = Config.Verification.VerifiedRoleId;
                        var token = !string.IsNullOrWhiteSpace(Config.Verification.BotToken)
                            ? Config.Verification.BotToken : Config.DiscordToServer.BotToken;
                        _ = Task.Run(async () =>
                        {
                            var ok = await DiscordBotService.RemoveGuildMemberRoleAsync(
                                token, guildId, removedDiscordId, roleId, Core.Logger);
                            if (!ok) Core.Logger.LogWarning("[DiscordUtilities] Failed to remove verified role from {DiscordId}", removedDiscordId);
                        });
                    }
                    player.SendChat(LocalizedChat("verification.unlinked"));

                }
                else
                {
                    player.SendChat(LocalizedChat("verification.not_linked"));
                }
                SendInvite(player);
            });
        }

        var token = !string.IsNullOrWhiteSpace(Config.Verification.BotToken)
            ? Config.Verification.BotToken
            : Config.DiscordToServer.BotToken;

        if (!string.IsNullOrWhiteSpace(token))
            _ = StartInteractionBotAsync(token);
        else
            Core.Logger.LogWarning("[DiscordUtilities] Bot token required for verification and report buttons");
        if (Config.Verification.Enabled && Config.CommandBlocker.Enabled) _ = PublishVerifyPromptAsync();
    }

    private IEnumerable<string> GetLinkCommands() => (new[] { Config.Verification.Commands.Link }.Concat(Config.Verification.Commands.LinkAliases))
        .Select(x => x.Trim().TrimStart('!', '/')).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    private IEnumerable<string> GetUnlinkCommands() => (new[] { Config.Verification.Commands.Unlink }.Concat(Config.Verification.Commands.UnlinkAliases))
        .Select(x => x.Trim().TrimStart('!', '/')).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    private string GetLinkCommand() => GetLinkCommands().FirstOrDefault() ?? "link";
    private string GetUnlinkCommand() => GetUnlinkCommands().FirstOrDefault() ?? "unlink";

    private string WithPrefix(object message) => Core.Localizer["prefix"].ToString() + " " + message;
    private string LocalizedChat(string key, params object[] args) => WithPrefix(Core.Localizer[key, args]);
    private void SendInvite(IPlayer player)
    {
        // Always the final line, including errors, already-linked and aliases.
        player.SendChat(LocalizedChat("discord_invite", Config.Verification.DiscordInvite ?? ""));
    }

    private async Task StartInteractionBotAsync(string token)
    {
        var channelId = Config.Verification.ChannelId;
        if (string.IsNullOrWhiteSpace(channelId))
            channelId = await Webhook.ResolveChannelIdAsync(Config.Verification.WebhookUrl);
        if (string.IsNullOrWhiteSpace(channelId))
            channelId = await Webhook.ResolveChannelIdAsync(Config.CallAdmin.WebhookUrl);
        if (string.IsNullOrWhiteSpace(channelId)) return;
        _verificationBot?.Dispose();
        _verificationBot = new DiscordBotService(Core.Logger, (_, _) => { }, () => Config.Debug,
            (ch, id, name, content) => OnVerificationDiscordMessage(token, ch, id, name, content),
            (customId, userId, userName, interactionId, interactionToken, messageId) =>
                OnDiscordButton(customId, userId, userName, interactionId, interactionToken, messageId));
        _verificationBot.Start(token, channelId);
    }

    private async Task PublishVerifyPromptAsync()
    {
        if (string.IsNullOrWhiteSpace(Config.Verification.WebhookUrl)) return;
        await _verifyPanelLock.WaitAsync();
        try
        {
            var token = !string.IsNullOrWhiteSpace(Config.Verification.BotToken)
                ? Config.Verification.BotToken : Config.DiscordToServer.BotToken;
            var channel = await Webhook.ResolveChannelIdAsync(Config.Verification.WebhookUrl);
            if (channel == null || string.IsNullOrWhiteSpace(token)) return;
            var embed = new DiscordEmbed
            {
                Title = DiscordText.Get("discord.commandblocker.verify_title", "Steam Account Linking"),
                Description = DiscordText.Get("discord.commandblocker.verify_description",
                    "1. Join the CS2 server.\n2. Enter !{0} in the game chat.\n3. Press the button below and enter the generated code.\n4. Your Steam and Discord accounts will be linked.", GetLinkCommand()),
                Color = WebhookService.ParseColor("#5865F2")
            };
            var statePath = Path.Combine(Core.PluginDataDirectory, "verify_panel_message_id.txt");
            try { if (File.Exists(statePath)) _verifyPanelMessageId = File.ReadAllText(statePath).Trim(); } catch { }
            if (string.IsNullOrWhiteSpace(_verifyPanelMessageId))
                _verifyPanelMessageId = await DiscordBotService.FindExistingPanelAsync(token, channel,
                    "discordutilities:verify_open", Core.Logger);
            var id = await DiscordBotService.UpsertInteractiveEmbedAsync(token, channel, embed,
                "discordutilities:verify_open", DiscordText.Get("discord.commandblocker.verify_button", "Link Steam Account"), _verifyPanelMessageId);
            if (id == null && _verifyPanelMessageId != null)
                Core.Logger.LogWarning("[DiscordUtilities] Existing verification panel {MessageId} could not be updated or recreated", _verifyPanelMessageId);
            if (id != null)
            {
                _verifyPanelMessageId = id;
                Directory.CreateDirectory(Core.PluginDataDirectory);
                File.WriteAllText(statePath, id);
            }
            else Core.Logger.LogWarning("[DiscordUtilities] Could not publish or update verification panel");
        }
        catch (Exception ex) { Core.Logger.LogError(ex, "[DiscordUtilities] Verification panel failed"); }
        finally { _verifyPanelLock.Release(); }
    }

    private void OnDiscordButton(string customId, string userId, string userName, string interactionId, string interactionToken, string messageId)
    {
        if (customId == "discordutilities:verify_open")
        {
            _ = DiscordBotService.OpenVerificationModalAsync(interactionId, interactionToken,
                DiscordText.Get("discord.verification.modal_title", "Link Steam Account"),
                DiscordText.Get("discord.verification.modal_code_label", "Verification code"), Core.Logger);
        }
        else if (customId == "discordutilities:verify_submit")
        {
            // The modal code is carried in the final callback argument for modal submissions.
            _ = ProcessModalVerificationAsync(userId, userName, messageId, interactionId, interactionToken);
        }
        else if (customId.StartsWith("discordutilities:bughandle:", StringComparison.Ordinal))
        {
            _ = HandleBugReportButtonAsync(customId, userId, userName, interactionId, interactionToken, messageId);
        }
        else if (customId.StartsWith("discordutilities:handle:", StringComparison.Ordinal))
        {
            _ = HandleReportButtonAsync(customId, userId, userName, interactionId, interactionToken, messageId);
        }
    }

    private async Task ProcessModalVerificationAsync(string userId, string userName, string code,
        string interactionId, string interactionToken)
    {
        // Reply privately to the modal; do not post verification results in the channel.
        var result = await VerifyDiscordCodeAsync(userId, userName, code);
        await DiscordBotService.AcknowledgeButtonAsync(interactionId, interactionToken, result, Core.Logger);
    }

    private void OnVerificationDiscordMessage(string token, string channelId, string authorId, string authorName, string content)
    {
        // Linking happens through the modal only. Ignore channel messages.
    }

    private async Task<string> VerifyDiscordCodeAsync(string authorId, string authorName, string code)
    {
        if (!_linkDatabaseReady)
            return DiscordText.Get("discord.verification.database_error", "Link database unavailable. Contact an administrator.");

        if (!_pendingVerifications.TryGetValue(code.Trim().ToUpperInvariant(), out var pending))
            return DiscordText.Get("discord.verification.invalid", "Invalid or expired verification code.");

        if (DateTime.UtcNow > pending.ExpiresAtUtc)
        {
            _pendingVerifications.TryRemove(code.Trim().ToUpperInvariant(), out _);
            return DiscordText.Get("discord.verification.expired", "Code expired. Run !link again.");
        }

        if (_linkedAccounts.Any(x => x.Key != pending.SteamId && x.Value == authorId))
            return DiscordText.Get("discord.verification.already_linked", "This Discord account is already linked.");

        var oldLink = _linkedAccounts.TryGetValue(pending.SteamId, out var previous) ? previous : null;
        _linkedAccounts[pending.SteamId] = authorId;
        if (!SaveLinkedAccounts())
        {
            if (oldLink == null) _linkedAccounts.TryRemove(pending.SteamId, out _);
            else _linkedAccounts[pending.SteamId] = oldLink;
            return DiscordText.Get("discord.verification.database_error", "Could not save account link.");
        }

        OnCommandBlockerLinkChanged(pending.SteamId, true);
        _pendingVerifications.TryRemove(code.Trim().ToUpperInvariant(), out _);
        if (!string.IsNullOrWhiteSpace(Config.Verification.GuildId) &&
            !string.IsNullOrWhiteSpace(Config.Verification.VerifiedRoleId))
        {
            _ = Task.Run(async () =>
            {
                var token = !string.IsNullOrWhiteSpace(Config.Verification.BotToken)
                    ? Config.Verification.BotToken : Config.DiscordToServer.BotToken;
                var ok = await DiscordBotService.AddGuildMemberRoleAsync(
                    token, Config.Verification.GuildId, authorId, Config.Verification.VerifiedRoleId, Core.Logger);
                if (!ok) Core.Logger.LogWarning("[DiscordUtilities] Could not assign verified role to {DiscordId}", authorId);
            });
        }

        Core.Scheduler.NextTick(() =>
        {
            foreach (var player in Core.PlayerManager.GetAllValidPlayers())
                if (player.IsValid && player.SteamID == pending.SteamId)
                {
                    player.SendChat(LocalizedChat("verification.linked_ingame", authorName));
                    break;
                }
        });
        return DiscordText.Get("discord.verification.success", "Successfully linked to {0}.", pending.PlayerName);
    }

    public bool IsPlayerLinked(ulong steamId) => _linkDatabaseReady && _linkedAccounts.ContainsKey(steamId);

    public string? GetDiscordId(ulong steamId) => _linkedAccounts.TryGetValue(steamId, out var id) ? id : null;

    // Uses the SwiftlyS2 database connection configured for mysql_detailed.
    // No independent SQLite database or duplicate MySQL credentials.
    private string GetLinksFilePath() => Path.Combine(Core.PluginDataDirectory, "verified_players.json");

    private IDbConnection OpenLinkDatabase()
    {
        var connection = Core.Database.GetConnection("mysql_detailed");
        if (connection.State != ConnectionState.Open) connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"CREATE TABLE IF NOT EXISTS discordutilities (
            steam_id VARCHAR(32) NOT NULL PRIMARY KEY,
            discord_id VARCHAR(32) NOT NULL UNIQUE,
            linked_at VARCHAR(40) NOT NULL
        )";
        command.ExecuteNonQuery();
        return connection;
    }

    private void LoadLinkedAccounts()
    {
        lock (_linksFileLock)
        {
            _linkDatabaseReady = false;
            try
            {
                using var db = OpenLinkDatabase();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT steam_id, discord_id FROM discordutilities";
                var loaded = new Dictionary<ulong, string>();
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        if (ulong.TryParse(reader.GetString(0), out var id))
                            loaded[id] = reader.GetString(1);
                _linkedAccounts.Clear();
                foreach (var entry in loaded) _linkedAccounts[entry.Key] = entry.Value;
                _linkDatabaseReady = true;

                // Import old JSON data only when the SQL table is empty.
                if (_linkedAccounts.IsEmpty && File.Exists(GetLinksFilePath()))
                {
                    var legacy = JsonSerializer.Deserialize<Dictionary<ulong,string>>(File.ReadAllText(GetLinksFilePath()));
                    if (legacy != null && legacy.Count > 0)
                    {
                        foreach (var item in legacy) _linkedAccounts[item.Key] = item.Value;
                        if (!SaveLinkedAccounts())
                        {
                            _linkedAccounts.Clear();
                            _linkDatabaseReady = false;
                            Core.Logger.LogError("[DiscordUtilities] Legacy link import failed");
                        }
                    }
                }
                Core.Logger.LogInformation("[DiscordUtilities] SwiftlyS2 mysql_detailed: discordutilities table ready; {Count} links loaded", _linkedAccounts.Count);
            }
            catch (Exception ex)
            {
                _linkDatabaseReady = false;
                Core.Logger.LogError(ex, "[DiscordUtilities] Cannot initialize discordutilities using SwiftlyS2 mysql_detailed. Check Swiftly database config and permissions.");
            }
        }
    }

    private bool SaveLinkedAccounts()
    {
        lock (_linksFileLock)
        {
            try
            {
                using var db = OpenLinkDatabase();
                using var transaction = db.BeginTransaction();
                using (var clear = db.CreateCommand())
                {
                    clear.Transaction = transaction;
                    clear.CommandText = "DELETE FROM discordutilities";
                    clear.ExecuteNonQuery();
                }
                foreach (var item in _linkedAccounts)
                {
                    using var command = db.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO discordutilities (steam_id,discord_id,linked_at) VALUES (@steam,@discord,@time)";
                    var steam = command.CreateParameter(); steam.ParameterName = "@steam"; steam.Value = item.Key.ToString(); command.Parameters.Add(steam);
                    var discord = command.CreateParameter(); discord.ParameterName = "@discord"; discord.Value = item.Value; command.Parameters.Add(discord);
                    var time = command.CreateParameter(); time.ParameterName = "@time"; time.Value = DateTime.UtcNow.ToString("O"); command.Parameters.Add(time);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
                return true;
            }
            catch (Exception ex)
            {
                Core.Logger.LogError(ex, "[DiscordUtilities] Failed to save links to SwiftlyS2 database");
                return false;
            }
        }
    }

    private static string GenerateVerificationCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var rng = Random.Shared;
        var buffer = new char[6];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = chars[rng.Next(chars.Length)];
        return new string(buffer);
    }
}
