using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Core.Menus.OptionsBase;
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
                                player.SendChat(msg);
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
                        player.SendChat(msg);
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
            .Where(p => p.IsValid && p.PlayerID != reporter.PlayerID)
            .ToList();

        if (otherPlayers.Count == 0)
        {
            var noPlayersMsg = Core.Localizer["calladmin.no_players"];
            reporter.SendChat(noPlayersMsg);
            return;
        }

        var config = Config.CallAdmin;
        var menuTitle = string.IsNullOrWhiteSpace(config.MenuTitle) ? "CallAdmin - Select Player" : config.MenuTitle;
        var builder = Core.MenusAPI.CreateBuilder().Design.SetMenuTitle(menuTitle);

        foreach (var target in otherPlayers)
        {
            var targetName = target.Name;
            var targetId = target.PlayerID;
            var targetSteamId = target.SteamID;

            var option = new ButtonMenuOption($"{targetName} (#{targetId})");
            option.Click += async (_, args) =>
            {
                await Core.Scheduler.NextTickAsync(() =>
                {
                    OpenCallAdminReasonMenu(args.Player, targetName, targetSteamId);
                });
            };

            builder.AddOption(option);
        }

        Core.MenusAPI.OpenMenuForPlayer(reporter, builder.Build());
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
            reporter.SendChat(msg);
            return;
        }

        if (_preconfiguredCallAdminReasons.Count == 0)
        {
            LoadCallAdminReasons();
        }

        var selectTitle = Core.Localizer["calladmin.select_reason", targetName];
        var builder = Core.MenusAPI.CreateBuilder().Design.SetMenuTitle(selectTitle);

        foreach (var item in _preconfiguredCallAdminReasons)
        {
            var option = new ButtonMenuOption(item.Title);
            var capturedReason = item;

            option.Click += async (_, args) =>
            {
                await Core.Scheduler.NextTickAsync(() =>
                {
                    if (IsCallAdminOnCooldown(args.Player, out var remaining))
                    {
                        var msg = Core.Localizer["calladmin.cooldown", remaining];
                        args.Player.SendChat(msg);
                        return;
                    }

                    if (capturedReason.Title.Contains("Other", StringComparison.OrdinalIgnoreCase) ||
                        capturedReason.Title.Contains("Custom", StringComparison.OrdinalIgnoreCase))
                    {
                        var prompt = Core.Localizer["calladmin.custom_prompt", targetName];
                        args.Player.SendChat(prompt);
                    }
                    else
                    {
                        var reasonText = string.IsNullOrWhiteSpace(capturedReason.Description)
                            ? capturedReason.Title
                            : $"{capturedReason.Title} ({capturedReason.Description})";

                        _ = Task.Run(() => SubmitCallAdminReportAsync(args.Player, targetName, targetSteamId, reasonText));
                    }
                });
            };

            builder.AddOption(option);
        }

        Core.MenusAPI.OpenMenuForPlayer(reporter, builder.Build());
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
                .FirstOrDefault(p => p.IsValid && (
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
                        reporter.SendChat(msg);
                });
                return;
            }

            if (reason.Length < config.MinimumReasonLength)
            {
                var msg = Core.Localizer["calladmin.reason_too_short", config.MinimumReasonLength];
                Core.Scheduler.NextTick(() =>
                {
                    if (reporter.IsValid)
                        reporter.SendChat(msg);
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
            var currentMap = _currentMapName;
            var cleanMap = currentMap;
            var lastSlash = currentMap.LastIndexOfAny(['/', '\\']);
            if (lastSlash >= 0 && lastSlash < currentMap.Length - 1)
                cleanMap = currentMap[(lastSlash + 1)..];

            string? reporterAvatar = null;
            if (reporterSteamId != 0)
                reporterAvatar = await SteamApi.GetPlayerAvatarAsync(reporterSteamId);

            var isHttpConnect = !string.IsNullOrWhiteSpace(connectAddress) &&
                                (connectAddress.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                 connectAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

            // Construct Discord Embed
            var embed = new DiscordEmbed
            {
                Title = $"🚨 CallAdmin Report #{reportId}",
                Color = WebhookService.ParseColor(config.EmbedColor),
                Author = new EmbedAuthor
                {
                    Name = $"{reporterName} ({reporterSteamId})",
                    IconUrl = reporterAvatar
                },
                Footer = new EmbedFooter { Text = $"{serverName} • Discord Utilities" }
            };
            embed.WithTimestamp();

            embed.AddField("Reporter", $"**{reporterName}**\n[`{reporterSteamId}`](https://steamcommunity.com/profiles/{reporterSteamId})", true);

            if (targetSteamId != 0)
            {
                embed.AddField("Target", $"**{targetName}**\n[`{targetSteamId}`](https://steamcommunity.com/profiles/{targetSteamId})", true);
            }
            else
            {
                embed.AddField("Target", $"**{targetName}**", true);
            }

            embed.AddField("Current Map", $"`{cleanMap}`", true);
            embed.AddField("Report Reason", $"```{reason}```", false);

            if (!string.IsNullOrWhiteSpace(connectAddress))
            {
                if (isHttpConnect)
                {
                    embed.AddField("Quick Connect", $"[👉 Click Here to Connect]({connectAddress})", false);
                }
                else
                {
                    embed.AddField("Quick Connect", $"`connect {connectAddress}`", false);
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
                                Label = "Connect to Server",
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

            await Webhook.SendEmbedAsync(
                config.WebhookUrl, 
                embed, 
                username: $"{serverName} CallAdmin",
                content: mentionContent,
                components: components);

            var sentMsg = Core.Localizer["calladmin.sent", config.CooldownSeconds];
            Core.Scheduler.NextTick(() =>
            {
                if (reporter.IsValid)
                    reporter.SendChat(sentMsg);
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
