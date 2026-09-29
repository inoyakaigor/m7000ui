[Русская версия](README.md) | [English version](README_EN.md)

# M7000 UI

A tray application for monitoring traffic on TP-Link routers with internet limits.
It is designed to work with the entire M7000, M7005, M7010, M7350, M7352, M7400, M7450, M7750, M8550 series, but has only been tested on the M7000.

## Description

The application runs in the system tray. Once a minute it logs in to the TP-Link router web interface (`192.168.0.1`) through its internal API and:

- draws a ring in the tray that shows the remaining package traffic;
- shows a Windows notification for every new SMS;
- shows battery charge, signal level, 4G and roaming in the settings window.

## Requirements

- **OS:** Windows 10 19041 (20H2) or later
- **.NET 10.0 SDK** — to build
- **Windows App Runtime 2.0** — must be installed on the system. The app does not bundle the Windows App SDK: in self-contained mode SDK 2.0 crashes when it registers notifications (it cannot find `Microsoft.WindowsAppRuntime.Insights.Resource.dll`).

## Project Structure

```
M7000 UI/
├── M7000 UI.slnx         # Solution (x64, ARM64)
├── M7000Lib/             # Library: TP-Link protocol, router communication
│   ├── TPLinkProtocol.cs
│   └── TPLinkModules.cs
└── M7000Tray/            # WinUI 3 tray app: icon, settings window, notifications
    ├── App.xaml / App.xaml.cs
    ├── SettingsWindow.xaml / SettingsWindow.xaml.cs
    ├── RouterClient.cs       # router session: status, SMS, mark as read
    ├── Settings.cs           # settings.ini, DPAPI password, auto-start
    ├── TrayIconRenderer.cs   # draws the tray icon
    ├── Glyphs.cs             # Segoe Fluent Icons glyphs: battery, signal
    └── Assets/
        ├── app.png           # source icon (for README and app-plate.png)
        ├── app-plate.png     # icon in the notification header
        ├── envelope.png      # picture in the SMS notification
        └── M7000.ico         # exe and window icon
```

## Build

```bash
dotnet build "M7000 UI.slnx" -p:Platform=x64
```

## Run from IDE / `dotnet run`

```bash
cd M7000Tray
dotnet run -p:Platform=x64
```

Only a tray icon appears — the app has no main window; settings open from the icon menu.

## Publishing

```bash
dotnet publish M7000Tray/M7000Tray.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o publish
```

`--self-contained true` puts the .NET runtime into the folder. Windows App Runtime 2.0 is still required on the system (see "Requirements").

> **Important:** in unpackaged mode (`WindowsPackageType=None`) `dotnet publish` drops the compiled XAML (`.pri`, `.xbf`). `M7000Tray.csproj` has a step that copies them into `publish` — without them the app crashes at startup.

## How It Works

1. **Startup.** A mutex keeps a second copy from starting. The icon appears in the tray and the first poll runs right away.
2. **Network check.** Before every poll the app gets the MAC of the device at `192.168.0.1` through ARP — no login and no HTTP, so the router does not spend a login attempt. The app remembers its router MAC on the first successful poll. If the MAC is different (you joined another network that also uses `192.168.0.1`), there is no poll and no login; the tray shows the plain app icon and the tooltip «Другая сеть — роутер M7000 не найден» (another network, M7000 not found). If nobody answers ARP, the tooltip says «Нет связи с роутером» (no connection to the router). When the network appears or changes (wake from sleep, another Wi-Fi), a poll runs after 3 seconds instead of waiting a minute.
3. **Router polling** once a minute: login, the `status` module (traffic, battery, signal, unread SMS count) and the first page of the SMS inbox. Router sessions run strictly one at a time.
4. **Tray icon.**
   - The ring is the remaining package traffic: full ring 100%, no ring 0%.
   - Ring color: sky blue 100–90%, green down to 40%, yellow down to 10%, red below.

   <img src="./tray-states.png" alt="Tray icon states">

   Left to right: battery below 10%, unread SMS, percent, percent while paused, no data, error.

   - Inside the ring the icon shows one of three things, by priority — when several conditions are true, the upper one wins:
     1. a red battery when the router battery is below 10% (most important: the router will turn off soon);
     2. a gold envelope when there are unread SMS; it goes away when the SMS is marked read;
     3. the remaining percent — everything is fine.
   - Pause is not a separate state but an overlay: while polling is paused, a white pause sign in the bottom right corner is drawn on top of any of the three.
   - A gray ring with "?" means no data yet (the first seconds after start).
   - A yellow triangle means an error or no connection: it replaces the whole icon, and the tooltip shows the reason.
   - The router icon (same as the exe) means the PC is on another network; the app waits for its router.
5. **Mouse.**
   - Left click opens the router admin page `http://192.168.0.1/login.html` and pauses polling for 5 minutes — otherwise the app poll would log you out of the admin page.
   - Right click opens the menu: «Настройки» (settings), «Обновить» (refresh), «Не обновлять» (pause for 5/10/20 minutes), «Выход» (exit).
6. **New SMS** — a Windows notification with the text and a «Пометить прочитанным» (mark as read) button. The button also works when the app is closed: Windows starts it.
7. **Wrong password** — only when the router clearly answers "wrong password" (`result 1`); if the login response cannot be read, login is retried in a new session. Polling stops until the password is saved again or «Обновить» (refresh) is pressed in the menu. After 10 failed logins the router blocks login for 2 hours, so the app does not retry.
8. **Auto-start** is on by default (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) and can be turned off with a checkbox in the settings window.

## Package limit and remaining traffic

The app counts how much of the package is used by itself: every minute it adds how much the router counter grew since the last poll. You set the package size and the starting point in the settings window.

- **Лимит пакета, ГБ** (package limit, GB) — the size of your operator package.
- **Осталось сейчас, ГБ** (remaining now, GB) — how much traffic is really left. The field shows the current remaining value; if it differs from the operator data (for example, from a balance SMS), type the correct number and press «Сохранить» (Save) — the app recalculates the usage.
- **Пакет продлён** (package renewed) — press it when the operator renewed the package or you bought a new one and the traffic is full again. The button sets "remaining" equal to the limit; then press «Сохранить» (Save) and the usage starts from zero. If the new package has a different size, change the package limit first.

While the limit or remaining fields are edited and not saved, the line above the progress bar and the bar itself show the typed values marked «не сохранено» (not saved). The tray icon and the settings file change only after Save.

## Settings

Stored in `%APPDATA%\Igor Zviagintsev\M7000\settings.ini`. You can edit the file by hand: the app picks up the change on the next poll.

| Parameter | Description |
|---|---|
| `PasswordProtected` | Web interface password, encrypted with DPAPI (only your Windows account can decrypt it) |
| `LimitGb` | Package limit, GB |
| `UsedBytes` | Used from the package, bytes (counted by the app) |
| `LastRouterTotal` | Last router traffic counter value, bytes |
| `RouterMac` | MAC of your router — remembered on the first successful poll, reset when a new password is saved |
| `LastSmsTime` | Time of the last SMS that was already notified |
| `AutoStart` | Start with Windows (`True`/`False`) |

The router address is fixed in code: `192.168.0.1` (`RouterClient.RouterUrl`).

## Troubleshooting

| Problem | Solution |
|---|---|
| App does not start or closes at once | Install Windows App Runtime 2.0 and check `crash.log` next to `settings.ini` |
| Yellow triangle in the tray | Hover the icon — the tooltip shows the reason; the full stack is in `crash.log` |
| «Роутер отклонил пароль» (password rejected) | The router answered "wrong password" (`result 1`). Check the password in the admin page and save it again in the settings window, or press «Обновить» (refresh) in the menu — that is one login attempt. If login is blocked, wait 2 hours |
| «Нет связи с роутером» (no connection) | The PC must be on the router network; the address is `192.168.0.1`. Network failures are not written to `crash.log` |
| «Другая сеть — роутер M7000 не найден» on your own network | The router was replaced or reset: save the password again in the settings window or delete `RouterMac` from `settings.ini` — the app remembers the new MAC |
| No icon in the tray | Windows may have moved it to hidden icons (^) — drag it to the taskbar |

## Acknowledgements

Big thanks to @vpaeder for his work on [TP-Link M7350 C++](https://github.com/vpaeder/tplink_m7350_cpp) project! Without it, I'd never have finished decoding TP-Link's protocol. Even with AI.

## License

GPLv3
