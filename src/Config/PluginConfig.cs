namespace DiscordUtilities.Config;

public sealed class PluginConfig
{
    public string SteamApiKey { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string ServerIP { get; set; } = "";
    public string ServerDNS { get; set; } = "";
    public MapNotificationConfig MapNotification { get; set; } = new();
    public ChatRelayConfig ChatRelay { get; set; } = new();
    public DiscordToServerConfig DiscordToServer { get; set; } = new();
    public AdminLogsConfig AdminLogs { get; set; } = new();
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
}

public sealed class ChatRelayConfig
{
    public bool Enabled { get; set; } = true;
    public string WebhookUrl { get; set; } = "";
    public bool UseSteamAvatars { get; set; } = true;
    public string UsernameFormat { get; set; } = "{PlayerName} [{SteamId2}]";
    public int CooldownSeconds { get; set; } = 1;
    public bool IgnoreCommands { get; set; } = true;
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
