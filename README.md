# Stalker Vodka GPS

Shows where you are in **S.T.A.L.K.E.R. 2: Heart of Chornobyl** on the live map at
[stalker.vodka/map](https://stalker.vodka/map).

The app runs next to the game, reads your position and the direction you're facing, and sends them
to the map in your browser. Your marker on the map then moves as you play.

## Requirements

- Windows 10 or 11 (64-bit)
- S.T.A.L.K.E.R. 2 (Steam or Game Pass)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- Microsoft Edge WebView2 Runtime (already included with Windows 11 and up-to-date Windows 10)

## Download

Download the latest version from the
[Releases page](https://github.com/Razzmatazzz/stalker-gps/releases). Extract it to a folder of your
choice and run `StalkerGPS.exe`.

## How to use it

1. **Start Stalker Vodka GPS.** You can start it before or after the game; it waits for the game and
   connects on its own.
2. **Play the game.** Once you're in the world, the status shows **Tracking** and your X, Y, Z
   coordinates and heading update as you move.
3. **Open [stalker.vodka/map](https://stalker.vodka/map)** in your browser. The map connects to the
   app and shows your position.
   - If the browser asks whether the site may access devices or apps on your local network, choose
     **Allow**. That's how the map talks to the app on your PC.

Leave the app running while you play. The map reconnects if you restart the app or the game.

### Status

| Status | Meaning |
|---|---|
| Waiting for game | The game isn't running. The app checks for it every few seconds. |
| Locating player | The game is running and the app is finding the player in memory. This can take a few seconds while the game starts. |
| No player | You're in a menu or on a loading screen. |
| Tracking | Your position is being read and sent to the map. |
| Error | Something went wrong. The app retries on its own. |

## Settings

Open **Settings** at the bottom of the window. Changes take effect when you click **Save**.

- **Minimize to tray:** minimizing hides the window to the notification area. Double-click the tray
  icon (or right-click it → **Show**) to bring it back; right-click → **Exit** to quit.
- **Start minimized:** the app opens minimized (to the tray, if that's on). Handy if you start it
  with Windows.
- **Map connection port:** the local port the map connects to (default `41347`). Only change this if
  the app reports that the port is already in use.
- **Position update interval / Check for the game every:** how often the app reads your position while
  you play and how often it looks for the game while it isn't running. The defaults are fine for
  most people.
- **Process names** and **manual pointer chain:** advanced options for troubleshooting. You shouldn't
  need them; the app finds the player automatically, including after game updates.

Settings are saved to `%LocalAppData%\StalkerGPS\settings.json`.

## Troubleshooting

- **The map says it can't connect.** Make sure the app is running, then reload the map. If you
  blocked the browser's local network prompt, allow it again in the site settings (click the icon
  next to the address bar).
- **"Port … is already in use."** Another program is using the port. Pick a different port in
  Settings.
- **Stuck on "Locating player".** Wait until you've loaded into the world. If it never changes after
  a game update, please open an issue.

## Privacy and safety

- The app only **reads** the game's memory. It never changes anything in the game, and it doesn't
  touch save files.
- Nothing is sent over the internet. The app accepts connections only from your own PC, and only
  from the stalker.vodka map page; other websites are refused.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet run --project StalkerGPS
```

## For map developers

The app runs a WebSocket server at `ws://localhost:41347` (configurable). It accepts a connection
only when the `Origin` header is exactly `https://stalker.vodka` or `http://localhost:3000`
(development); anything else gets `403 Forbidden`.

Messages are JSON text frames. The current status (and position, if any) is sent right after
connecting; after that, only changes are sent.

```json
{"type":"status","status":"tracking"}
{"type":"position","x":236349.65,"y":157956.05,"z":1116.08,"yaw":315.59}
```

- `status` is one of `tracking`, `waitingForGame`, `locating`, `noPlayer`, `error`, `stopped`.
- `x`, `y`, `z` are game (Unreal Engine) units, in centimeters.
- `yaw` is the camera heading in degrees, 0 to 360: 0 faces +X and 90 faces +Y (clockwise seen from
  above). It's `null` when unknown.
