using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using DiscordUtilities.Services;

namespace DiscordUtilities;

public partial class DiscordUtilities
{
    private DiscordBotService? _verificationBot;
    private readonly ConcurrentDictionary<string, PendingVerification> _pendingVerifications = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ulong, string> _linkedAccounts = new();
    private readonly object _linksFileLock = new();
    private bool _verificationInitialized;

    private sealed record PendingVerification(ulong SteamId, string PlayerName, DateTime ExpiresAtUtc);

    internal void InitializeVerification()
    {
        if (!Config.Verification.Enabled)
        {
            _verificationBot?.Stop();
            return;
        }

        LoadLinkedAccounts();

        if (!_verificationInitialized)
        {
            _verificationInitialized = true;

            var linkCmd = string.IsNullOrWhiteSpace(Config.Verification.LinkCommand) ? "link" : Config.Verification.LinkCommand.TrimStart('!', '/');
            var unlinkCmd = string.IsNullOrWhiteSpace(Config.Verification.UnlinkCommand) ? "unlink" : Config.Verification.UnlinkCommand.TrimStart('!', '/');

            Core.Command.RegisterCommand(linkCmd, (context) =>
            {
                if (!Config.Verification.Enabled) return;
                var player = context.Sender;
                if (player == null || !player.IsValid || player.IsFakeClient) return;

                if (_linkedAccounts.TryGetValue(player.SteamID, out var existingDiscordId))
                {
                    player.SendChat(Core.Localizer["verification.already_linked", existingDiscordId, unlinkCmd]);
                    return;
                }

                var code = GenerateVerificationCode();
                var expiryMinutes = Math.Max(1, Config.Verification.CodeExpiryMinutes);
                _pendingVerifications[code] = new PendingVerification(player.SteamID, player.Controller?.PlayerName ?? "Unknown", DateTime.UtcNow.AddMinutes(expiryMinutes));

                player.SendChat(Core.Localizer["verification.code_generated", code]);
                player.SendChat(Core.Localizer["verification.code_instructions", code, expiryMinutes]);
            });

            Core.Command.RegisterCommand(unlinkCmd, (context) =>
            {
                if (!Config.Verification.Enabled) return;
                var player = context.Sender;
                if (player == null || !player.IsValid || player.IsFakeClient) return;

                if (_linkedAccounts.TryRemove(player.SteamID, out var removedDiscordId))
                {
                    SaveLinkedAccounts();
                    player.SendChat(Core.Localizer["verification.unlinked"]);
                    if (!string.IsNullOrWhiteSpace(Config.Verification.WebhookUrl))
                    {
                        _ = Webhook.SendEmbedAsync(
                            Config.Verification.WebhookUrl,
                            new DiscordEmbed
                            {
                                Title = "Account Unlinked",
                                Description = $"**Player:** {player.Controller?.PlayerName ?? "Unknown"} (`{player.SteamID}`)\n**Discord ID:** `{removedDiscordId}`",
                                Color = WebhookService.ParseColor("#FFA500")
                            }
                        );
                    }
                }
                else
                {
                    player.SendChat(Core.Localizer["verification.not_linked"]);
                }
            });
        }

        var token = !string.IsNullOrWhiteSpace(Config.Verification.BotToken)
            ? Config.Verification.BotToken
            : Config.DiscordToServer.BotToken;

        if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(Config.Verification.ChannelId))
        {
            _verificationBot?.Dispose();
            _verificationBot = new DiscordBotService(
                Core.Logger,
                (_, _) => { },
                () => Config.Debug,
                (channelId, authorId, authorName, content) => OnVerificationDiscordMessage(token, channelId, authorId, authorName, content)
            );
            _verificationBot.Start(token, Config.Verification.ChannelId);
        }
        else
        {
            _verificationBot?.Stop();
        }
    }

    private void OnVerificationDiscordMessage(string token, string channelId, string authorId, string authorName, string content)
    {
        if (!Config.Verification.Enabled) return;

        var trimmed = content.Trim();
        string? code = null;
        if (trimmed.StartsWith("!link ", StringComparison.OrdinalIgnoreCase))
            code = trimmed[6..].Trim();
        else if (trimmed.StartsWith("!verify ", StringComparison.OrdinalIgnoreCase))
            code = trimmed[8..].Trim();

        if (string.IsNullOrWhiteSpace(code)) return;

        if (!_pendingVerifications.TryRemove(code, out var pending))
        {
            _ = DiscordBotService.SendChannelMessageAsync(token, channelId, $"<@{authorId}> Invalid or expired verification code.", Core.Logger);
            return;
        }

        if (DateTime.UtcNow > pending.ExpiresAtUtc)
        {
            _ = DiscordBotService.SendChannelMessageAsync(token, channelId, $"<@{authorId}> That verification code has expired. Please run `!link` in-game again.", Core.Logger);
            return;
        }

        _linkedAccounts[pending.SteamId] = authorId;
        SaveLinkedAccounts();

        _ = DiscordBotService.SendChannelMessageAsync(token, channelId, $"<@{authorId}> Successfully linked to **{pending.PlayerName}** (`{pending.SteamId}`).", Core.Logger);

        if (!string.IsNullOrWhiteSpace(Config.Verification.GuildId) && !string.IsNullOrWhiteSpace(Config.Verification.VerifiedRoleId))
        {
            _ = Task.Run(async () =>
            {
                var ok = await DiscordBotService.AddGuildMemberRoleAsync(token, Config.Verification.GuildId, authorId, Config.Verification.VerifiedRoleId, Core.Logger);
                if (!ok)
                {
                    await DiscordBotService.SendChannelMessageAsync(
                        token,
                        channelId,
                        $"<@{authorId}> Failed to assign the verified role (missing bot permissions or role hierarchy). Please contact a server admin.",
                        Core.Logger
                    );
                }
            });
        }

        if (!string.IsNullOrWhiteSpace(Config.Verification.WebhookUrl))
        {
            _ = Webhook.SendEmbedAsync(
                Config.Verification.WebhookUrl,
                new DiscordEmbed
                {
                    Title = "Account Linked",
                    Description = $"**Player:** {pending.PlayerName} (`{pending.SteamId}`)\n**Discord:** {authorName} (`{authorId}`)",
                    Color = WebhookService.ParseColor("#57F287")
                }
            );
        }

        Core.Scheduler.NextTick(() =>
        {
            foreach (var p in Core.PlayerManager.GetAllValidPlayers())
            {
                if (p.IsValid && p.SteamID == pending.SteamId)
                {
                    p.SendChat(Core.Localizer["verification.linked_ingame", authorName]);
                    break;
                }
            }
        });
    }

    public bool IsPlayerLinked(ulong steamId) => _linkedAccounts.ContainsKey(steamId);

    public string? GetDiscordId(ulong steamId) => _linkedAccounts.TryGetValue(steamId, out var id) ? id : null;

    private string GetLinksFilePath() => Path.Combine(Core.PluginDataDirectory, "verified_players.json");

    private void LoadLinkedAccounts()
    {
        try
        {
            var path = GetLinksFilePath();
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<ulong, string>>(json);
            if (data == null) return;
            _linkedAccounts.Clear();
            foreach (var kv in data)
                _linkedAccounts[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[DiscordUtilities] Failed to load verified_players.json");
        }
    }

    private void SaveLinkedAccounts()
    {
        lock (_linksFileLock)
        {
            try
            {
                var path = GetLinksFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var json = JsonSerializer.Serialize(_linkedAccounts.ToDictionary(k => k.Key, v => v.Value), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Core.Logger.LogError(ex, "[DiscordUtilities] Failed to save verified_players.json");
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
