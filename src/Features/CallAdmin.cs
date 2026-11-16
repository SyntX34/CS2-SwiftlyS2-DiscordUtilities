using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Menu.Components;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using DiscordUtilities.Services;

namespace DiscordUtilities;

public class CallAdminReasonItem
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}

public class CallAdminReasonsConfig
{
    public List<CallAdminReasonItem> Reasons { get; set; } = [];
}

public partial class DiscordUtilities
{
    private readonly ConcurrentDictionary<ulong, DateTime> _callAdminCooldowns = new();
    private List<CallAdminReasonItem> _preconfiguredCallAdminReasons = [];
    private static int _reportCounter = 0;

    internal void InitializeCallAdmin()
    {
        if (!Config.CallAdmin.Enabled) return;

        if (string.IsNullOrWhiteSpace(Config.CallAdmin.WebhookUrl))
        {
            Core.Logger.LogWarning("[DiscordUtilities] CallAdmin enabled but no webhook URL set");
            return;
        }

        LoadCallAdminReasons();

        // Hook chat for !calladmin, /calladmin, !report, /report
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
                    var cmd = parts[0].ToLowerInvariant();
                    if (cmd is "calladmin" or "report")
                    {
                        var args = parts.Length > 1 ? parts[1].Trim() : "";
                        var player = Core.PlayerManager.GetPlayer(playerId);
                        if (player != null && player.IsValid)
                        {
                            if (IsCallAdminOnCooldown(player, out var remaining))
                            {
                                var msg = Core.Localizer["calladmin.cooldown", remaining];
                                player.SendChat(WithPrefix(msg));
                                return HookResult.Handled;
                            }

                            if (string.IsNullOrWhiteSpace(args))
                            {
                                OpenCallAdminTargetMenu(player);
                            }
                            else
                            {
                                _ = Task.Run(() => HandleCallAdminCommandAsync(playerId, args));
                            }
                        }
                        return HookResult.Handled;
                    }
                }
            }

            return HookResult.Continue;
        });

        // Hook console command sw_calladmin / !calladmin / /calladmin
        Core.Command.HookClientCommand((playerId, commandLine) =>
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return HookResult.Continue;

            var parts = commandLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return HookResult.Continue;

            var cmd = parts[0].ToLowerInvariant().TrimStart('!', '/');
            if (cmd.StartsWith("sw_") || cmd.StartsWith("css_") || cmd.StartsWith("sm_"))
                cmd = cmd[3..];

            if (cmd is "calladmin" or "report")
            {
                var args = parts.Length > 1 ? parts[1].Trim() : "";
                var player = Core.PlayerManager.GetPlayer(playerId);
                if (player != null && player.IsValid)
                {
                    if (IsCallAdminOnCooldown(player, out var remaining))
                    {
                        var msg = Core.Localizer["calladmin.cooldown", remaining];
                        player.SendChat(WithPrefix(msg));
                        return HookResult.Handled;
                    }

                    if (string.IsNullOrWhiteSpace(args))
                    {
                        OpenCallAdminTargetMenu(player);
                    }
                    else
                    {
                        _ = Task.Run(() => HandleCallAdminCommandAsync(playerId, args));
                    }
                }
                return HookResult.Handled;
            }

            return HookResult.Continue;
        });

        Core.Logger.LogInformation("[DiscordUtilities] CallAdmin registered with Menu and JSONC reason system");
    }

    private void LoadCallAdminReasons()
    {
        try
        {
            var reasonsPath = Core.Configuration.GetConfigPath("calladmin_reasons.jsonc");
            if (File.Exists(reasonsPath))
            {
                var json = File.ReadAllText(reasonsPath);
                var options = new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true
                };

                var doc = JsonSerializer.Deserialize<CallAdminReasonsConfig>(json, options);
                if (doc?.Reasons != null && doc.Reasons.Count > 0)
                {
                    _preconfiguredCallAdminReasons = doc.Reasons;
                    Core.Logger.LogInformation("[DiscordUtilities] Loaded {Count} preconfigured calladmin reasons", _preconfiguredCallAdminReasons.Count);
                    return;
                }
            }

            // Fallback default reasons
            _preconfiguredCallAdminReasons =
            [
                new CallAdminReasonItem { Title = "Cheating / Wallhack / Aimbot", Description = "Suspected cheat usage" },
                new CallAdminReasonItem { Title = "Mic / Voice Chat Spam", Description = "Mic spam or noise" },
                new CallAdminReasonItem { Title = "Toxicity / Harassment", Description = "Verbal abuse or hate speech" },
                new CallAdminReasonItem { Title = "Team Killing / Griefing", Description = "Griefing or trolling" },
                new CallAdminReasonItem { Title = "Other / Custom Report", Description = "Type !calladmin <player> <reason>" }
            ];
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[DiscordUtilities] Failed to read calladmin_reasons.jsonc");
        }
    }

    private void OpenCallAdminTargetMenu(IPlayer reporter)
    {
        if (!reporter.IsValid) return;

        var otherPlayers = Core.PlayerManager.GetAllValidPlayers()
            .Where(p => p.IsValid && !p.IsFakeClient)
            .ToList();

        if (otherPlayers.Count == 0)
        {
            var noPlayersMsg = Core.Localizer["calladmin.no_players"];
            reporter.SendChat(WithPrefix(noPlayersMsg));
            return;
        }

        var config = Config.CallAdmin;
        var menuTitle = string.IsNullOrWhiteSpace(config.MenuTitle) ? "CallAdmin - Select Player" : config.MenuTitle;
        var builder = Core.Menu.CreateMenu("discordutilities.calladmin.players").WithTitle(menuTitle);

        foreach (var target in otherPlayers)
        {
            var targetName = target.Name;
            var targetId = target.PlayerID;
            var targetSteamId = target.SteamID;

            var option = new ButtonComponent($"{targetName} (#{targetId})", ctx =>
            {
                Core.Scheduler.NextTick(() =>
                {
                    OpenCallAdminReasonMenu(ctx.Player, targetName, targetSteamId);
                });
                return ValueTask.CompletedTask;
            });

            builder.Add(option);
        }

        builder.Build().Open(reporter);
    }

    private bool IsCallAdminOnCooldown(IPlayer player, out int remainingSeconds)
    {
        remainingSeconds = 0;
        if (!player.IsValid) return false;

        var config = Config.CallAdmin;
        if (config.CooldownSeconds <= 0 || player.SteamID == 0) return false;

        if (_callAdminCooldowns.TryGetValue(player.SteamID, out var lastCall))
        {
            var elapsed = (DateTime.UtcNow - lastCall).TotalSeconds;
            if (elapsed < config.CooldownSeconds)
            {
                remainingSeconds = (int)Math.Ceiling(config.CooldownSeconds - elapsed);
                return true;
            }
        }

        return false;
    }

    private void OpenCallAdminReasonMenu(IPlayer reporter, string targetName, ulong targetSteamId)
    {
        if (!reporter.IsValid) return;

        if (IsCallAdminOnCooldown(reporter, out var rem))
        {
            var msg = Core.Localizer["calladmin.cooldown", rem];
            reporter.SendChat(WithPrefix(msg));
            return;
        }

        if (_preconfiguredCallAdminReasons.Count == 0)
        {
            LoadCallAdminReasons();
        }

        var selectTitle = Core.Localizer["calladmin.select_reason", targetName];
        var builder = Core.Menu.CreateMenu("discordutilities.calladmin.reasons").WithTitle(selectTitle);

        foreach (var item in _preconfiguredCallAdminReasons)
        {
            var capturedReason = item;
            var option = new ButtonComponent(item.Title, ctx =>
            {
                Core.Scheduler.NextTick(() =>
                {
                    if (IsCallAdminOnCooldown(ctx.Player, out var remaining))
                    {
                        var msg = Core.Localizer["calladmin.cooldown", remaining];
                        ctx.Player.SendChat(WithPrefix(msg));
                        return;
                    }

                    if (capturedReason.Title.Contains("Other", StringComparison.OrdinalIgnoreCase) ||
                        capturedReason.Title.Contains("Custom", StringComparison.OrdinalIgnoreCase))
                    {
                        var prompt = Core.Localizer["calladmin.custom_prompt", targetName];
                        ctx.Player.SendChat(WithPrefix(prompt));
                    }
                    else
                    {
                        var reasonText = string.IsNullOrWhiteSpace(capturedReason.Description)
                            ? capturedReason.Title
                            : $"{capturedReason.Title} ({capturedReason.Description})";

                        _ = Task.Run(() => SubmitCallAdminReportAsync(ctx.Player, targetName, targetSteamId, reasonText));
                    }
                });
                return ValueTask.CompletedTask;
            });

            builder.Add(option);
        }

        builder.Build().Open(reporter);
    }

    private async Task HandleCallAdminCommandAsync(int reporterPlayerId, string rawArgs)
    {
        var reporter = Core.PlayerManager.GetPlayer(reporterPlayerId);
        if (reporter == null || !reporter.IsValid) return;

        // Parse target and reason
        string targetName = "None / Server Issue";
        ulong targetSteamId = 0;
        string reason = rawArgs;

        var argParts = rawArgs.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (argParts.Length > 1)
        {
            var potentialTargetName = argParts[0];
            var foundPlayer = Core.PlayerManager.GetAllValidPlayers()
                .FirstOrDefault(p => p.IsValid && !p.IsFakeClient && (
                    p.Name.Contains(potentialTargetName, StringComparison.OrdinalIgnoreCase) ||
                    p.SteamID.ToString() == potentialTargetName ||
                    p.PlayerID.ToString() == potentialTargetName));

            if (foundPlayer != null)
            {
                targetName = foundPlayer.Name;
                targetSteamId = foundPlayer.SteamID;
                reason = argParts[1].Trim();
            }
        }

        await SubmitCallAdminReportAsync(reporter, targetName, targetSteamId, reason);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, DiscordEmbed> _reportMessages = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _handledReports = new();

    private async Task HandleReportButtonAsync(string customId, string userId, string userName,
        string interactionId, string interactionToken, string messageId)
    {
        if (!long.TryParse(customId["discordutilities:handle:".Length..], out var reportId)) return;
        if (!_reportMessages.TryGetValue(reportId, out var embed))
        {
            await DiscordBotService.AcknowledgeButtonAsync(interactionId, interactionToken,
                "Report state unavailable after plugin reload.", Core.Logger);
            return;
        }
        if (!_handledReports.TryAdd(reportId, 1))
        {
            await DiscordBotService.AcknowledgeButtonAsync(interactionId, interactionToken,
                "This report is already being handled.", Core.Logger);
            return;
        }
        await DiscordBotService.AcknowledgeButtonAsync(interactionId, interactionToken, null, Core.Logger);
        var updated = new DiscordEmbed
        {
            Title = embed.Title, Description = embed.Description, Color = 0x57F287,
            Url = embed.Url, Thumbnail = embed.Thumbnail, Image = embed.Image,
            Footer = embed.Footer, Author = embed.Author, Fields = embed.Fields,
            Timestamp = embed.Timestamp
        };
        updated.AddField(DiscordText.Get("discord.calladmin.status", "Status"),
            DiscordText.Get("discord.calladmin.handled", "Handled by {0}", $"<@{userId}>"), false);
        var token = !string.IsNullOrWhiteSpace(Config.Verification.BotToken)
            ? Config.Verification.BotToken : Config.DiscordToServer.BotToken;
        var channel = await Webhook.ResolveChannelIdAsync(Config.CallAdmin.WebhookUrl);
        var saved = await DiscordBotService.UpsertInteractiveEmbedAsync(token, channel ?? "", updated,
            $"discordutilities:handle:{reportId}", DiscordText.Get("discord.calladmin.handled", "Handled by {0}", userName), messageId);
        if (saved == null) _handledReports.TryRemove(reportId, out _);
    }

    private async Task SubmitCallAdminReportAsync(IPlayer reporter, string targetName, ulong targetSteamId, string reason)
    {
        try
        {
            var config = Config.CallAdmin;
            if (!reporter.IsValid) return;

            ulong reporterSteamId = reporter.SteamID;
            var reporterName = reporter.Name;

            // Check Cooldown
            if (IsCallAdminOnCooldown(reporter, out var remainingSec))
            {
                var msg = Core.Localizer["calladmin.cooldown", remainingSec];
                Core.Scheduler.NextTick(() =>
                {
                    if (reporter.IsValid)
                        reporter.SendChat(WithPrefix(msg));
                });
                return;
            }

            if (reason.Length < config.MinimumReasonLength)
            {
                var msg = Core.Localizer["calladmin.reason_too_short", config.MinimumReasonLength];
                Core.Scheduler.NextTick(() =>
                {
                    if (reporter.IsValid)
                        reporter.SendChat(WithPrefix(msg));
                });
                return;
            }

            // Update cooldown
            if (reporterSteamId != 0)
            {
                _callAdminCooldowns[reporterSteamId] = DateTime.UtcNow;
            }

            var reportId = Interlocked.Increment(ref _reportCounter);
            var serverName = GetServerDisplayName();
            var connectAddress = GetServerConnectAddress();
            string? reporterAvatar = null;
            if (reporterSteamId != 0)
                reporterAvatar = await SteamApi.GetPlayerAvatarAsync(reporterSteamId);

            var isHttpConnect = !string.IsNullOrWhiteSpace(connectAddress) &&
                                (connectAddress.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                 connectAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

            // Construct Discord Embed
            var embed = new DiscordEmbed
            {
                Title = DiscordText.Get("discord.calladmin.title", "🚨 CallAdmin Report #{0}", reportId),
                Color = WebhookService.ParseColor(config.EmbedColor),
                Author = new EmbedAuthor
                {
                    Name = $"{reporterName} ({reporterSteamId})",
                    IconUrl = reporterAvatar
                },
                Footer = new EmbedFooter { Text = $"{serverName} • Discord Utilities" }
            };
            embed.WithTimestamp();

            embed.AddField(DiscordText.Get("discord.calladmin.reporter", "Reporter"), $"**{reporterName}**\n[`{reporterSteamId}`](https://steamcommunity.com/profiles/{reporterSteamId})", true);

            if (targetSteamId != 0)
            {
                embed.AddField(DiscordText.Get("discord.calladmin.target", "Target"), $"**{targetName}**\n[`{targetSteamId}`](https://steamcommunity.com/profiles/{targetSteamId})", true);
            }
            else
            {
                embed.AddField(DiscordText.Get("discord.calladmin.target", "Target"), $"**{targetName}**", true);
            }

            embed.AddField(DiscordText.Get("discord.calladmin.reason", "Report Reason"), $"```{reason}```", false);

            if (!string.IsNullOrWhiteSpace(connectAddress))
            {
                if (isHttpConnect)
                {
                    embed.AddField(DiscordText.Get("discord.calladmin.quick_connect", "🔗 Quick Connect"), $"[{DiscordText.Get("discord.calladmin.connect_link", "👉 Click Here to Connect")}]({connectAddress})", false);
                }
                else
                {
                    embed.AddField(DiscordText.Get("discord.calladmin.quick_connect", "🔗 Quick Connect"), $"`connect {connectAddress}`", false);
                }
            }

            if (!string.IsNullOrWhiteSpace(config.BannerUrl))
            {
                embed.Thumbnail = new EmbedImage { Url = config.BannerUrl };
            }

            // Connect button for admins
            List<DiscordComponentActionRow>? components = null;
            if (!string.IsNullOrWhiteSpace(connectAddress))
            {
                var buttonUrl = isHttpConnect 
                    ? connectAddress 
                    : $"https://vauff.com/connect.php?ip={connectAddress.Replace("connect ", "", StringComparison.OrdinalIgnoreCase).Trim()}";

                components =
                [
                    new DiscordComponentActionRow
                    {
                        Components =
                        [
                            new DiscordComponentButton
                            {
                                Label = DiscordText.Get("discord.calladmin.connect_button", "Connect to Server"),
                                Url = buttonUrl,
                                Emoji = new DiscordEmoji { Name = "🚨" }
                            }
                        ]
                    }
                ];
            }

            string? mentionContent = string.IsNullOrWhiteSpace(config.MentionRoleOrUser) 
                ? null 
                : config.MentionRoleOrUser.Trim();

            _reportMessages[reportId] = embed;
            var botToken = !string.IsNullOrWhiteSpace(Config.Verification.BotToken)
                ? Config.Verification.BotToken : Config.DiscordToServer.BotToken;
            var reportChannel = await Webhook.ResolveChannelIdAsync(config.WebhookUrl);
            var interactiveMessageId = await DiscordBotService.UpsertInteractiveEmbedAsync(botToken, reportChannel ?? "",
                embed, $"discordutilities:handle:{reportId}", DiscordText.Get("discord.calladmin.handle", "Handle"));
            if (interactiveMessageId == null)
            {
                Core.Logger.LogWarning("[DiscordUtilities] Report button unavailable: configure a bot token with Send Messages permission. Sending plain webhook report.");
                await Webhook.SendEmbedAsync(config.WebhookUrl, embed, username: $"{serverName} CallAdmin",
                    content: string.IsNullOrWhiteSpace(config.MentionRoleOrUser) ? null : config.MentionRoleOrUser.Trim(), components: components);
            }
            var sentMsg = Core.Localizer["calladmin.sent", config.CooldownSeconds];
            Core.Scheduler.NextTick(() =>
            {
                if (reporter.IsValid)
                    reporter.SendChat(WithPrefix(sentMsg));
            });
            Core.Logger.LogInformation("[DiscordUtilities] CallAdmin Report #{ReportId} sent by {Reporter} against {Target} for: {Reason}",
                reportId, reporterName, targetName, reason);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[DiscordUtilities] CallAdmin execution failed");
        }
    }
}
