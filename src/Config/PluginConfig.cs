namespace DiscordUtilities.Config;

public sealed class PluginConfig
{
    public bool Debug { get; set; } = false;
    public string SteamApiKey { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string ServerIP { get; set; } = "";
    public string ServerDNS { get; set; } = "";
    public MapNotificationConfig MapNotification { get; set; } = new();
    public ChatRelayConfig ChatRelay { get; set; } = new();
    public DiscordToServerConfig DiscordToServer { get; set; } = new();
    public AdminLogsConfig AdminLogs { get; set; } = new();
    public CallAdminConfig CallAdmin { get; set; } = new();
    public BugReportConfig BugReport { get; set; } = new();
    public PlayerJoinedConfig PlayerJoined { get; set; } = new();
    public VerificationConfig Verification { get; set; } = new();
    public CommandBlockerConfig CommandBlocker { get; set; } = new();
    public DiscordRconConfig DiscordRcon { get; set; } = new();
}

public sealed class MapNotificationConfig
{
    public bool Enabled { get; set; } = true;
    public string WebhookUrl { get; set; } = "";
    public string BannerUrl { get; set; } = "";
    public string EmbedColor { get; set; } = "#5865F2";
    public bool ShowWorkshopId { get; set; } = true;
    public bool ShowPlayerCount { get; set; } = true;
    public bool ShowServerIP { get; set; } = true;
    public int CooldownSeconds { get; set; } = 10;
    public int RefreshSeconds { get; set; } = 30;
}

public sealed class ChatRelayConfig
{
    public bool Enabled { get; set; } = true;
    public string WebhookUrl { get; set; } = "";
    public bool UseSteamAvatars { get; set; } = true;
    public string UsernameFormat { get; set; } = "{PlayerName}";
    public string MessageFormat { get; set; } = "{Message}";
    public string TeamMessageFormat { get; set; } = "(TEAM) {Message}";
    public int CooldownSeconds { get; set; } = 1;
    public bool IgnoreCommands { get; set; } = true;
    public List<string> IgnoredPrefixes { get; set; } = ["!", "/", "rtv", "nominate", "nextmap", "timeleft", "currentmap"];
    public bool IgnoreTeamChat { get; set; } = false;
}

public sealed class DiscordToServerConfig
{
    public bool Enabled { get; set; } = false;
    public string BotToken { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string ChatPrefix { get; set; } = "[Discord]";
    public string ChatColor { get; set; } = "#7289DA";
    public string TagFormat { get; set; } = "{ChatPrefix} {DiscordName}: {Message}";
}

public sealed class AdminLogsConfig
{
    public bool Enabled { get; set; } = true;
    public string WebhookUrl { get; set; } = "";
    public string BannerUrl { get; set; } = "";
    public string EmbedColor { get; set; } = "#ED4245";
    public bool IgnoreConsole { get; set; } = true;
    public bool LogCommands { get; set; } = true;
    public bool LogMapChanges { get; set; } = true;
    public bool LogCvarChanges { get; set; } = true;
    public bool LogRcon { get; set; } = true;
    public int CooldownSeconds { get; set; } = 2;
}

public sealed class CallAdminConfig
{
    public bool Enabled { get; set; } = true;
    public string WebhookUrl { get; set; } = "";
    public string MentionRoleOrUser { get; set; } = ""; // e.g. "<@&ROLE_ID>" or "@here"
    public string BannerUrl { get; set; } = "";
    public string EmbedColor { get; set; } = "#FFA500";
    public string MenuTitle { get; set; } = "CallAdmin - Select Player / Reason";
    public int CooldownSeconds { get; set; } = 30;
    public int MinimumReasonLength { get; set; } = 3;
}

public sealed class BugReportConfig
{
    public bool Enabled { get; set; } = true;
    public string WebhookUrl { get; set; } = "";
    public string MentionRoleOrUser { get; set; } = ""; // e.g. "<@&ROLE_ID>" or "@here"
    public string BannerUrl { get; set; } = "";
    public string EmbedColor { get; set; } = "#FFFF00";
    public string MenuTitle { get; set; } = "Report a Bug / Issue";
    public int CooldownSeconds { get; set; } = 30;
    public int MinimumReasonLength { get; set; } = 3;
}

public class PlayerJoinedConfig
{
    public bool Enabled { get; set; } = false;

    public string WebhookUrl { get; set; } = string.Empty;

    public int CooldownSeconds { get; set; } = 2;

    public string JoinEmbedColor { get; set; } = "#57F287";

    public string LeaveEmbedColor { get; set; } = "#ED4245";

    public bool ShowSteamId { get; set; } = false;

    public bool ShowSteamProfile { get; set; } = true;

    public bool ShowPlayerCount { get; set; } = true;

    public bool ShowPlayerSlot { get; set; } = false;

    public bool ShowConnectionTime { get; set; } = true;

    public bool ShowDisconnectReason { get; set; } = true;
}

public sealed class VerificationCommandsConfig
{
    public string Link { get; set; } = "link";
    public List<string> LinkAliases { get; set; } = ["discord", "dc", "verify"];
    public string Unlink { get; set; } = "unlink";
    public List<string> UnlinkAliases { get; set; } = ["unverify"];
}

public sealed class VerificationConfig
{
    public string DiscordInvite { get; set; } = "";
    public bool Enabled { get; set; } = false;
    public string BotToken { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string GuildId { get; set; } = "";
    public string VerifiedRoleId { get; set; } = "";
    public VerificationCommandsConfig Commands { get; set; } = new();
    public string LinkCommand { get; set; } = "link";
    public string UnlinkCommand { get; set; } = "unlink";
    public int CodeExpiryMinutes { get; set; } = 5;
    public string WebhookUrl { get; set; } = "";
}

public sealed class CommandBlockerConfig
{
    public bool Enabled { get; set; } = false;
    public List<string> BlockedCommands { get; set; } = ["kill", "explode"];
}

public sealed class DiscordRconConfig
{
    public bool Enabled { get; set; } = false;
    public string BotToken { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string CommandPrefix { get; set; } = "!rcon ";
    public string ConsoleMessageFormat { get; set; } = "[red][Console]: [red]{Message}[default]";
    public List<string> AllowedDiscordUserIds { get; set; } = [];
}
