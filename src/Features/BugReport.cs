using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Core.Menus.OptionsBase;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using DiscordUtilities.Services;

namespace DiscordUtilities;

public class BugReportReasonItem
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}

public class BugReportReasonsConfig
{
    public List<BugReportReasonItem> Reasons { get; set; } = [];
}

public partial class DiscordUtilities
{
    private readonly ConcurrentDictionary<ulong, DateTime> _bugReportCooldowns = new();
    private List<BugReportReasonItem> _preconfiguredBugReasons = [];
    private static int _bugReportCounter = 0;

    internal void InitializeBugReport()
    {
        if (!Config.BugReport.Enabled) return;

        if (string.IsNullOrWhiteSpace(Config.BugReport.WebhookUrl))
        {
            Core.Logger.LogWarning("[DiscordUtilities] BugReport enabled but no webhook URL set");
            return;
        }

        LoadBugReportReasons();

        // Hook chat commands: !bug, /bug, !bugreport, /bugreport
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
                    if (cmd is "bug" or "bugs" or "bugreport" or "reportbug")
                    {
                        var args = parts.Length > 1 ? parts[1].Trim() : "";
                        var player = Core.PlayerManager.GetPlayer(playerId);
                        if (player != null && player.IsValid)
                        {
                            if (string.IsNullOrWhiteSpace(args))
                            {
                                OpenBugReportMenu(player);
                            }
                            else
                            {
                                _ = Task.Run(() => SubmitBugReportAsync(player, args));
                            }
                        }
                        return HookResult.Handled;
                    }
                }
            }

            return HookResult.Continue;
        });

        // Hook console commands: bug, bugreport, sw_bug, sw_bugreport
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

            if (cmd is "bug" or "bugs" or "bugreport" or "reportbug")
            {
                var args = parts.Length > 1 ? parts[1].Trim() : "";
                var player = Core.PlayerManager.GetPlayer(playerId);
                if (player != null && player.IsValid)
                {
                    if (string.IsNullOrWhiteSpace(args))
                    {
                        OpenBugReportMenu(player);
                    }
                    else
                    {
                        _ = Task.Run(() => SubmitBugReportAsync(player, args));
                    }
                }
                return HookResult.Handled;
            }

            return HookResult.Continue;
        });

        Core.Logger.LogInformation("[DiscordUtilities] BugReport registered for !bug, !bugreport, and console commands with Menu system");
    }

    private void LoadBugReportReasons()
    {
        try
        {
            var reasonsPath = Core.Configuration.GetConfigPath("bug_reasons.jsonc");
            if (File.Exists(reasonsPath))
            {
                var json = File.ReadAllText(reasonsPath);
                var options = new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true
                };

                var doc = JsonSerializer.Deserialize<BugReportReasonsConfig>(json, options);
                if (doc?.Reasons != null && doc.Reasons.Count > 0)
                {
                    _preconfiguredBugReasons = doc.Reasons;
                    Core.Logger.LogInformation("[DiscordUtilities] Loaded {Count} preconfigured bug report reasons", _preconfiguredBugReasons.Count);
                    return;
                }
            }

            // Fallback default reasons if file not yet present
            _preconfiguredBugReasons =
            [
                new BugReportReasonItem { Title = "Map Glitch / Stuck Spot", Description = "Stuck spot, exploit, or visual bug on current map" },
                new BugReportReasonItem { Title = "Texture / Visual Bug", Description = "Missing textures or lighting issues" },
                new BugReportReasonItem { Title = "Server Lag / FPS Drops", Description = "Experiencing server lag or stutter" },
                new BugReportReasonItem { Title = "Plugin / Feature Issue", Description = "Plugin or command malfunction" },
                new BugReportReasonItem { Title = "Other / Custom Report", Description = "Custom issue - use !bug <description>" }
            ];
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[DiscordUtilities] Failed to read bug_reasons.jsonc");
        }
    }

    private void OpenBugReportMenu(IPlayer player)
    {
        if (!player.IsValid) return;

        var config = Config.BugReport;
        var menuTitle = string.IsNullOrWhiteSpace(config.MenuTitle) ? "Report a Bug / Issue" : config.MenuTitle;
        var builder = Core.MenusAPI.CreateBuilder().Design.SetMenuTitle(menuTitle);

        if (_preconfiguredBugReasons.Count == 0)
        {
            LoadBugReportReasons();
        }

        foreach (var item in _preconfiguredBugReasons)
        {
            var option = new ButtonMenuOption(item.Title);
            var capturedItem = item;

            option.Click += async (_, args) =>
            {
                await Core.Scheduler.NextTickAsync(() =>
                {
                    if (capturedItem.Title.Contains("Other", StringComparison.OrdinalIgnoreCase) ||
                        capturedItem.Title.Contains("Custom", StringComparison.OrdinalIgnoreCase))
                    {
                        var prompt = Core.Localizer["bugreport.custom_prompt"];
                        args.Player.SendChat(prompt);
                    }
                    else
                    {
                        var detail = string.IsNullOrWhiteSpace(capturedItem.Description) 
                            ? capturedItem.Title 
                            : $"{capturedItem.Title} ({capturedItem.Description})";

                        _ = Task.Run(() => SubmitBugReportAsync(args.Player, detail));
                    }
                });
            };

            builder.AddOption(option);
        }

        Core.MenusAPI.OpenMenuForPlayer(player, builder.Build());
    }

    private async Task SubmitBugReportAsync(IPlayer reporter, string reason)
    {
        try
        {
            var config = Config.BugReport;
            if (!reporter.IsValid) return;

            ulong reporterSteamId = reporter.SteamID;
            var reporterName = reporter.Name;

            // Check Cooldown
            if (config.CooldownSeconds > 0 && reporterSteamId != 0)
            {
                var now = DateTime.UtcNow;
                if (_bugReportCooldowns.TryGetValue(reporterSteamId, out var lastCall) &&
                    (now - lastCall).TotalSeconds < config.CooldownSeconds)
                {
                    var remaining = (int)(config.CooldownSeconds - (now - lastCall).TotalSeconds);
                    var msg = Core.Localizer["bugreport.cooldown", remaining];
                    reporter.SendChat(msg);
                    return;
                }
            }

            if (reason.Length < config.MinimumReasonLength)
            {
                var msg = Core.Localizer["bugreport.reason_too_short", config.MinimumReasonLength];
                reporter.SendChat(msg);
                return;
            }

            // Update cooldown
            if (reporterSteamId != 0)
            {
                _bugReportCooldowns[reporterSteamId] = DateTime.UtcNow;
            }

            var reportId = Interlocked.Increment(ref _bugReportCounter);
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
                Title = $"🐛 Bug Report #{reportId}",
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
            embed.AddField("Map", $"`{cleanMap}`", true);
            embed.AddField("Bug Description / Reason", $"```{reason}```", false);

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

            // Connect button
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
                                Emoji = new DiscordEmoji { Name = "🛠️" }
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
                username: $"{serverName} BugReport",
                content: mentionContent,
                components: components);

            var sentMsg = Core.Localizer["bugreport.sent", config.CooldownSeconds];
            reporter.SendChat(sentMsg);
            Core.Logger.LogInformation("[DiscordUtilities] BugReport #{ReportId} sent by {Reporter} on map {Map}: {Reason}",
                reportId, reporterName, cleanMap, reason);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[DiscordUtilities] BugReport submission failed");
        }
    }
}
