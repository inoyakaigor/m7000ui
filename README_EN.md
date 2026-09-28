[Русская версия](README.md) | [English version](README_EN.md)

# M7000 UI

A tray application for monitoring traffic on TP-Link routers with internet limits.
It is designed to work with the entire M7000, M7005, M7010, M7350, M7352, M7400, M7450, M7750, M8550 series, but has only been tested on the M7000.

## Description

The application runs in the system tray, automatically checks used traffic on a TP-Link router through its internal API, and notifies about remaining gigabytes. It also displays system notifications when new SMS messages are received from the router.

## Requirements

- **OS:** Windows 10 19041 (20H2) or later
- **.NET 10.0 SDK** (versions 10.0.111 / 10.0.201 / 10.0.303 installed)
- **Windows App SDK 2.0** (pulled in via NuGet)

## Project Structure

```
M7000 UI/
├── M7000 UI.slnx         # Solution (x64, ARM64)
├── M7000Lib/             # Library: TP-Link protocol, router communication
│   ├── TPLinkProtocol.cs
│   └── TPLinkModules.cs
└── M7000Tray/            # TUI: system tray, settings, notifications
    ├── App.xaml / App.xaml.cs
    ├── SettingsWindow.xaml / SettingsWindow.xaml.cs
    ├── RouterClient.cs
    ├── Settings.cs
    ├── TrayIconRenderer.cs
    └── Assets/
        ├── app.png
        ├── envelope.png
        └── M7000.ico
```

## Build

```bash
# Restore packages and build Debug
dotnet build

# Build Release
dotnet build -c Release

# Restore packages and build Release
dotnet restore && dotnet build -c Release
```

## Run from IDE / `dotnet run`

```bash
cd M7000Tray
dotnet run
```

Runs the application in debug mode. A tray icon and settings window will open.

## Publishing (portable / self-contained)

```bash
# Portable build (requires .NET 10 runtime to be installed)
dotnet publish -c Release -r win-x64 --self-contained false -o publish

# Self-contained build (includes runtime)
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

> **Important:** When publishing in `WindowsPackageType="None"` (unpackaged) mode, XAML resources (.pri, .xbf) are automatically copied to the `publish` folder; without them the application crashes at startup.

## How It Works

1. **Startup** → The application creates a mutex (single-instance), places an icon in the tray.
2. **Router polling** → Every minute, `RouterClient.PollAsync()` queries used traffic.
3. **New SMS** → Displays a Windows toast notification (AppNotification).
4. **Settings** → Left-click on the icon opens the settings window (WebView2): router IP, username, password.
5. **Auto-start** → On first launch, it offers to add itself to startup.

## Package limit and remaining traffic

The app counts how much of the package is used by itself: every minute it adds how much the router counter grew since the last poll. You set the package size and the starting point in the settings window.

- **Лимит пакета, ГБ** (package limit, GB) — the size of your operator package.
- **Осталось сейчас, ГБ** (remaining now, GB) — how much traffic is really left. The field shows the current remaining value; if it differs from the operator data (for example, from a balance SMS), type the correct number and press «Сохранить» (Save) — the app recalculates the usage.
- **Пакет продлён** (package renewed) — press it when the operator renewed the package or you bought a new one and the traffic is full again. The button sets "remaining" equal to the limit; then press «Сохранить» (Save) and the usage starts from zero. If the new package has a different size, change the package limit first.

While the limit or remaining fields are edited and not saved, the line above the progress bar and the bar itself show the typed values marked «не сохранено» (not saved). The tray icon and the settings file change only after Save.

## Settings

Settings are stored in `settings.json` (path determined via `Settings.Load()`).

| Parameter | Description |
|---|---|
| `RouterIp` | Router IP address (default `192.168.1.1`) |
| `PasswordProtected` | Admin web interface password |
| `LimitGb` | Traffic limit in GB |
| `UsedBytes` | Used bytes (updated automatically) |

## Troubleshooting

| Problem | Solution |
|---|---|
| Application won't start | Make sure .NET 10.0 Desktop Runtime is installed |
| Settings window won't open | Check that WebView2 runtime is installed |
| Router connection error | Check IP and password in settings; the router must be on the same network |

## Acknowledgements

Big thanks to @vpaeder for his work on [TP-Link M7350 C++](https://github.com/vpaeder/tplink_m7350_cpp) project! Without it, I'd never have finished decoding TP-Link's protocol. Even with AI.

## License

GPLv3
