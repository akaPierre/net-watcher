# Net Watcher

Windows desktop app that keeps an inventory of every device on an office network:
IP, MAC, manufacturer, hostname, device type, and whether the device is approved. It alerts when an unknown device appears or an IP conflict is detected.

Discovery is non-invasive. It uses only standard lookups that every device answers anyway:
ARP, ping, NetBIOS name, mDNS/Bonjour name, reverse DNS, and a TCP connect test on a few
well-known ports to guess the device type. It never logs in to anything or reads traffic.

## Install

Two release files are produced in `dist\` (see *Build a release* below). Neither needs .NET installed.

| File | Use it when |
|---|---|
| `NetWatcher-Setup-<version>.msi` | Normal install. Double-click, accept the terms, Install. No admin rights needed. Adds Start menu and desktop shortcuts and an entry in *Settings → Apps* to uninstall. Installing a newer MSI upgrades in place (it closes the running app first) and keeps your data and settings. |
| `NetWatcher-<version>-portable.exe` | No install: copy the single file anywhere (e.g. a USB stick) and run it. |

Both use the same data folder, so you can switch between them.

**"Windows protected your PC" warning:** the files are not code-signed, so SmartScreen shows this
the first time. Click *More info → Run anyway*. Removing the warning requires a paid code-signing certificate.

Uninstalling removes the program, shortcuts and the start-with-Windows entry, but **keeps**
`%LOCALAPPDATA%\NetWatcher` (device registry, history, backups). Delete that folder to remove all data.

## Build a release

Requires the .NET 10 SDK. The WiX installer toolset is fetched automatically from NuGet.

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

The version comes from `<Version>` in `src/NetWatcher/NetWatcher.csproj`; override with `-Version 1.2.3`.
Always raise the version for a new release so the MSI upgrades older installs.

## Website

`website/` is a one-page site that presents the app (with an interactive preview using sample data)
and offers the downloads. `build.ps1` copies the release files into `website/downloads/` and writes
`release.json` (version, sizes, SHA-256), which the page reads to fill in its download buttons.

Preview it locally:

```bash
node website/serve.js
```

Then open http://localhost:8080. To publish, upload the whole `website` folder (including
`downloads/`) to any static host: an office web server, IIS, Netlify, Cloudflare Pages or similar.
The installers are about 65 MB each, which is too large for some free hosts. In that case host the
files elsewhere (e.g. GitHub Releases) and change the links in `website/app.js`.

## Run from source

```bash
dotnet run --project src/NetWatcher
```

Start hidden in the tray:

```bash
NetWatcher.exe --minimized
```

## Using it

- **Devices** tab: everything ever seen. Red rows are not approved yet. Double-click a row to
  register it: name, owner, location, type, reserved IP, notes, and the *Approved* checkbox.
  Right-click to approve several devices at once, copy IP/MAC, or forget a device.
- **IP map** tab: all 254 addresses. Free, approved, unknown, reserved, or in conflict.
- **Internet & critical** tab: router and internet response time, uptime, an outage history
  (exportable to CSV as evidence for the operator), and the status of your critical devices.
- **Events** tab: new devices, unknown devices coming online, IP changes, conflicts, outages, online/offline history.
- **Settings**: start with Windows, close-to-tray, scan interval, network adapter / scan range,
  which notifications to show, check interval and internet test addresses, data retention and backups.
- Closing the window keeps it running in the tray (unless turned off in Settings). Use the tray icon's menu to exit.

### Critical devices
Open a device and tick **Critical device**. It is checked every minute with a fresh ARP request
plus a ping, and you get a notification when it stops answering (after 2 failed checks in a row,
so a single lost packet never raises an alarm) and again when it comes back. Give critical
devices a reserved IP on the router (DHCP reservation) so they keep the same address.

### Internet health
Every minute the app pings the router and the internet test addresses (default `1.1.1.1`, `8.8.8.8`).
- **Router down**: the office network or the router itself is the problem.
- **Internet down (router OK)**: the router answers but the internet does not, so the problem is on the operator's side.

The outage table records start, end and duration of each.

### Muting
A device with **Mute notifications** ticked never pops a notification; everything is still logged in Events.

On the first run every device is imported quietly as "not approved". Register the office
devices once; from then on anything new triggers a Windows notification.

### IP conflict detection
- **Reserved IP taken**: you set a reserved IP on a device and a different MAC is using it.
- **Flapping**: the same IP is answered by device A, then B, then A again across scans.
  That's the signature of two devices configured with the same IP.

### Phones and private MACs
Modern phones use a random "private Wi-Fi address" per network, so they show up as
*Private MAC* with no manufacturer and may get a new MAC over time. To keep staff phones
recognized, turn off the private address for the office Wi-Fi on each phone.

## Data

Stored in `%LOCALAPPDATA%\NetWatcher\` (tray menu → *Open data folder*):
- `netwatcher.db`: SQLite database (devices, IP history, events, internet health, outages, settings)
- `oui.txt`: manufacturer list (downloaded from the Wireshark/IEEE registry, refreshed every 60 days)
- `backups\`: a copy of the database every 7 days (the last 8 are kept). Settings → *Back up now* makes one on demand.
  To restore, exit the app and copy a backup over `netwatcher.db`.

Old history is trimmed automatically: events after 180 days, internet history after 90 days (configurable).

## Translating

All UI text is in `src/NetWatcher/Resources/Strings.en.xaml`. To add Portuguese, copy it to
`Strings.pt-BR.xaml`, translate the values, and switch with `Loc.SetLanguage("pt-BR")`.

## Roadmap
- Switch port per device via SNMP (needs a managed switch)
- Read the router's DHCP lease table
- Remote alerts (Telegram / email)
- Per-device online timeline
- Portuguese UI
