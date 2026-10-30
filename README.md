# RemoteCraft

A clean-room remote desktop app for Windows, modelled on the AnyDesk 5 layout and workflow. It contains no AnyDesk code or assets and does not talk to AnyDesk's network.

## Features
- **This Desk** has a 9-digit desk ID. The **Remote Desk** card connects to another desk. **Browse Files** opens a files-only session.
- **Discovery** finds other RemoteCraft clients on the LAN automatically. **Recent sessions** are shown as thumbnails, with online state, favourites (★), aliases and three view modes.
- The session tab has a toolbar with:
  - chat;
  - display options: fit or 1:1, quality and monitor;
  - actions: Ctrl+Alt+Del, Task Manager, the Win key, Alt+Tab, lock, refresh and screenshot;
  - view-only mode, clipboard sync, a two-pane file manager, full screen (F11) and disconnect.
- The host side has an Accept/Dismiss window with live permission toggles (keyboard/mouse, clipboard, files, lock), chat and Disconnect.
- **Unattended access** uses a password. The viewer can tick "log in automatically", which stores a DPAPI-protected key, never the password.
- Security: ECDH P-256 key agreement plus AES-256-GCM on every session. A security code is shown while connecting. After 5 wrong passwords, logins are locked for 1 minute.
- You can connect by desk ID on the LAN, by `IP[:port]`, or by desk ID through a relay (`RemoteCraft.Relay`).
- The app can install itself per user, with Start menu and desktop shortcuts and autostart in the tray.

## Build
```
dotnet build -c Release
```
- App: `src\RemoteCraft\bin\Release\net8.0-windows\RemoteCraft.exe`
- Relay: `src\RemoteCraft.Relay\bin\Release\net8.0\RemoteCraft.Relay.exe [port]` (the default port is 7180)

## Installer
```
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```
This produces `dist\RemoteCraft-Setup-<version>.exe`. It is built with Inno Setup 6 and bundles a self-contained .NET 8 runtime, so the target PC doesn't need .NET.
- It installs per user (no admin prompt) by default. You can choose "all users" in the wizard instead.
- It installs the app plus `RemoteCraft.Relay.exe`.
- Optional tasks are a desktop shortcut, starting with Windows, and a Windows Firewall rule (all-users installs only).
- Uninstalling keeps your settings in `%APPDATA%\RemoteCraft`.

## Ports
| Port | Use |
|---|---|
| TCP 7171 | Direct sessions. If 7171 is busy, the next free port is used. |
| UDP 7170 | LAN discovery |
| TCP 7180 | Relay |

## Testing on one PC
`--profile NAME` runs a separate copy with its own ID, settings and port, for example `RemoteCraft.exe --profile B`. Other flags are `--connect <id|ip>` and `--tray`.

## Limits
- Ctrl+Alt+Del only works where the SoftwareSASGeneration policy allows it.
- The UAC secure desktop and windows of elevated apps can't be controlled unless RemoteCraft runs elevated.
- There is no Windows service yet, so you can't connect before someone logs in.
- No audio.
