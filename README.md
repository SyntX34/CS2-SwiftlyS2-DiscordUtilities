<div align="center">
  <img src="https://pan.samyyc.dev/s/VYmMXE" />
  <h2><strong>Discord Utilities</strong></h2>
  <h3>Full-featured Discord integration plugin for Counter-Strike 2 servers running SwiftlyS2.</h3>
</div>

<p align="center">
  <a href="https://github.com/SyntX34/CS2-SwiftlyS2-DiscordUtilities/releases"><img src="https://img.shields.io/badge/version-1.1.6-blue.svg" alt="Version"></a>
  <a href="https://github.com/swiftly-solution/swiftlys2"><img src="https://img.shields.io/badge/framework-SwiftlyS2-orange.svg" alt="Framework"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green.svg" alt="License"></a>
</p>

## Features

| Feature | Description | Webhook |
|---------|-------------|---------|
| 🗺️ **Map Notifications** | Rich embeds on map change — workshop detection, preview images from Steam API, player count, custom banners | Separate webhook |
| 💬 **Chat Relay** | Server chat → Discord with player Steam avatars, per-player cooldowns, command filtering | Separate webhook |
| 🔒 **Admin Logs** | Logs admin commands (ban/kick/mute/slap/map changes/cvar/rcon) as compact one-line Discord messages, with optional console filtering | Separate webhook |
| 🚨 **CallAdmin** | In-game player reporting (`!calladmin`, `!report`, `sw_calladmin`) to Discord with target detection, cooldowns, and role pings | Separate webhook |
| 🐛 **Bug Report** | In-game bug reporting menu (`!bug`, `!bugreport`) with customizable preconfigured reasons (`bug_reasons.jsonc`) & custom details | Separate webhook |
| 🔄 **Discord → Server** | Relay Discord channel messages into in-game chat — requires bot token and channel ID | Separate channel |
| 🔗 **Verification (Linking)** | Link Steam accounts to Discord via `!link` / `!verify` codes, with Discord role assignment, persistent links, and an interactive verification panel | Bot channel / Webhook |
| 🚫 **Command Blocker** | Block specified client commands until the player links their Steam and Discord accounts | Separate webhook |
| 🖥️ **Discord RCON** | Execute server console commands (e.g. mute, gag, kick, ban) directly from Discord for authorized user IDs | Bot channel |

### Feature Previews

<details open>
<summary><b>🗺️ Map Change Notifications</b></summary>
<br>

One continuously updated Discord status message (30-second refresh by default) with the current map, player count, and connection details. Uses map artwork when available and falls back to the configured banner.

<p align="center">
  <a href="readme/images/map_notification.png" target="_blank">
    <img src="readme/images/map_notification.png" alt="Map Notification Preview" width="650">
  </a>
</p>
</details>

<details open>
<summary><b>💬 In-Game Chat Relay</b></summary>
<br>

Live server chat relayed straight to Discord using player Steam profile avatars and formatted names. Features per-player cooldown protection and configurable command filtering to keep chat clean.

<p align="center">
  <a href="readme/images/relay.png" target="_blank">
    <img src="readme/images/relay.png" alt="Chat Relay Preview" width="650">
  </a>
</p>
</details>

<details open>
<summary><b>🔒 Admin Action Logs</b></summary>
<br>

Compact, single-line admin command logs with no large embed or Steam profile preview. Command categories and message text are translatable.

<p align="center">
  <a href="readme/images/admin_logs.png" target="_blank">
    <img src="readme/images/admin_logs.png" alt="Admin Logs Preview" width="650">
  </a>
</p>
</details>

<details open>
<summary><b>🚨 CallAdmin & Player Reports</b></summary>
<br>

Allows players to call server administrators directly to Discord via `!calladmin` or `!report`. Features an interactive SwiftlyS2 in-game menu to select players and reasons (configured in `calladmin_reasons.jsonc`), cooldown limits, role mentions (`@here` / role IDs), one-click server connection details, and a Handle button.

<p align="center">
  <a href="readme/images/calladmin.png" target="_blank">
    <img src="readme/images/calladmin.png" alt="CallAdmin Preview" width="650">
  </a>
</p>
</details>

<details open>
<summary><b>🐛 Bug & Issue Reporting</b></summary>
<br>

In-game reporting system via `!bug` or `!bugreport`. Players can choose from preconfigured bug categories (`bug_reasons.jsonc`) or submit custom issue descriptions directly to Discord. Reports include a Handle button for staff.

<p align="center">
  <a href="readme/images/bugreport.png" target="_blank">
    <img src="readme/images/bugreport.png" alt="Bug Report Preview" width="650">
  </a>
</p>
</details>

## Requirements

- **SwiftlyS2** framework installed on your CS2 server
- **Steam Web API Key** — [Get one here](https://steamcommunity.com/dev/apikey) (for player avatars & workshop map images)
- **Discord Webhook URLs** — Create webhooks in your Discord server channel settings
- **Discord bot token** — Needed for Discord → Server, verification, and Discord RCON features
- **MySQL connection** — Configure SwiftlyS2 `mysql_detailed` for persistent Steam–Discord account links

## Installation

1. Download the latest release from [Releases](https://github.com/SyntX34/CS2-SwiftlyS2-DiscordUtilities/releases)
2. Extract into your server's `addons/swiftlys2/plugins/` directory
3. Start the server — `config.jsonc` will be auto-generated at:
   ```
   addons/swiftlys2/configs/plugins/DiscordUtilities/config.jsonc
   ```
4. Configure your webhook URLs and Steam API key in `config.jsonc`
5. Restart the server or hot-reload the plugin

## Configuration

The plugin uses **JSONC** (JSON with Comments) for configuration, auto-generated on first run. Some settings may require a plugin reload, particularly bot and verification settings.

### Configuration example

This example includes all settings from the current configuration model. Set webhook URLs, bot credentials, and IDs for the features you enable. The plugin also generates `config.jsonc` automatically on first start.

```jsonc
{
  "DiscordUtilities": {
    "Debug": false,
    "SteamApiKey": "",
    "ServerName": "",
    "ServerIP": "",
    "ServerDNS": "",
    "MapNotification": {
      "Enabled": true,
      "WebhookUrl": "",
      "BannerUrl": "",
      "EmbedColor": "#5865F2",
      "ShowWorkshopId": true,
      "ShowPlayerCount": true,
      "ShowServerIP": true,
      "CooldownSeconds": 10,
      "RefreshSeconds": 30
    },
    "ChatRelay": {
      "Enabled": true,
      "WebhookUrl": "",
      "UseSteamAvatars": true,
      "UsernameFormat": "{PlayerName}",
      "MessageFormat": "{Message}",
      "TeamMessageFormat": "(TEAM) {Message}",
      "CooldownSeconds": 1,
      "IgnoreCommands": true,
      "IgnoredPrefixes": [
        "!",
        "/",
        "rtv",
        "nominate",
        "nextmap",
        "timeleft",
        "currentmap"
      ],
      "IgnoreTeamChat": false
    },
    "DiscordToServer": {
      "Enabled": false,
      "BotToken": "",
      "ChannelId": "",
      "ChatPrefix": "[Discord]",
      "ChatColor": "#7289DA",
      "TagFormat": "{ChatPrefix} {DiscordName}: {Message}"
    },
    "AdminLogs": {
      "Enabled": true,
      "WebhookUrl": "",
      "BannerUrl": "",
      "EmbedColor": "#ED4245",
      "IgnoreConsole": true,
      "LogCommands": true,
      "LogMapChanges": true,
      "LogCvarChanges": true,
      "LogRcon": true,
      "CooldownSeconds": 2
    },
    "CallAdmin": {
      "Enabled": true,
      "WebhookUrl": "",
      "MentionRoleOrUser": "",
      "BannerUrl": "",
      "EmbedColor": "#FFA500",
      "MenuTitle": "CallAdmin - Select Player / Reason",
      "CooldownSeconds": 30,
      "MinimumReasonLength": 3
    },
    "BugReport": {
      "Enabled": true,
      "WebhookUrl": "",
      "MentionRoleOrUser": "",
      "BannerUrl": "",
      "EmbedColor": "#FFFF00",
      "MenuTitle": "Report a Bug / Issue",
      "CooldownSeconds": 30,
      "MinimumReasonLength": 3
    },
    "PlayerJoined": {
      "Enabled": false,
      "WebhookUrl": "",
      "CooldownSeconds": 2,
      "JoinEmbedColor": "#57F287",
      "LeaveEmbedColor": "#ED4245",
      "ShowSteamId": false,
      "ShowSteamProfile": true,
      "ShowPlayerCount": true,
      "ShowPlayerSlot": false,
      "ShowConnectionTime": true,
      "ShowDisconnectReason": true
    },
    "Verification": {
      "DiscordInvite": "",
      "Enabled": false,
      "BotToken": "",
      "ChannelId": "",
      "GuildId": "",
      "VerifiedRoleId": "",
      "Commands": {
        "Link": "link",
        "LinkAliases": [
          "discord",
          "dc",
          "verify"
        ],
        "Unlink": "unlink",
        "UnlinkAliases": [
          "unverify"
        ]
      },
      "LinkCommand": "link",
      "UnlinkCommand": "unlink",
      "CodeExpiryMinutes": 5,
      "WebhookUrl": ""
    },
    "CommandBlocker": {
      "Enabled": false,
      "BlockedCommands": [
        "kill",
        "explode"
      ]
    },
    "DiscordRcon": {
      "Enabled": false,
      "BotToken": "",
      "ChannelId": "",
      "CommandPrefix": "!rcon ",
      "ConsoleMessageFormat": "[red][Console]: [red]{Message}[default]",
      "AllowedDiscordUserIds": []
    }
  }
}
```

For CallAdmin and BugReport role mentions, set `MentionRoleOrUser` to `<@&ROLE_ID>` (not just the numeric ID). The bot must have permission to mention that role, or the role must be mentionable. The **Handle** button retrieves the existing Discord message, so it also works after a plugin/server restart.

### Per-Feature Webhooks

Each feature uses its own webhook URL, so you can route notifications to different Discord channels:

- `#map-updates` — Map notifications
- `#chat-log` — Chat relay
- `#admin-log` — Admin actions
- `#call-admin` — Player reports / CallAdmin alerts
- `#bug-reports` — Bug & issue reports



### Additional Features

- **Verification:** Players use `!link` (or configured aliases) to receive a code and complete linking through a Discord button/modal. `!unlink` removes the link and the configured verified role. Set `Verification.VerifiedRoleId`, `GuildId`, `BotToken`, and `ChannelId` as needed; linked accounts are stored using SwiftlyS2's `mysql_detailed` database connection.
- **Discord → Server:** Relays messages from the configured Discord channel into CS2 chat. Configure `DiscordToServer.BotToken` and `ChannelId`.
- **Command Blocker:** Restricts configured commands until the player's Steam account is linked.
- **Translations:** Game and Discord messages use the SwiftlyS2 Core language (`resources/translations/<language>.jsonc`), falling back to English for missing keys.

## Building

```bash
dotnet build
dotnet publish -c Release
```

The output will be in `build/publish/DiscordUtilities/` with a zip file at `build/DiscordUtilities.zip`.

## Credits

- Inspired by [Discord Utilities](https://github.com/NockyCZ/CS2-Discord-Utilities) for CounterStrikeSharp
- Built on the [SwiftlyS2](https://github.com/swiftly-solution/swiftlys2) framework
