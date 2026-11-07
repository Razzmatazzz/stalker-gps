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

## Map overlay

The **Map** section has an **Open Map in Browser** button, which opens the map already connected to
the app and in the app's language.

The overlay shows the map in a small window over the game. Turn it on under **Map**, where you can
also set its size, opacity and screen corner. Run the game in borderless mode; the overlay
can't appear over exclusive fullscreen. It only shows while the game or this app is the window in
front, and hides while a game menu such as the inventory or PDA is open, which the app tells from
the game showing its mouse cursor, unless the mouse is on the overlay.

Clicks pass through the overlay to the game, so you control the map with hotkeys, or from the **Map
controls** tray that opens on the right of the app while the overlay is on. The button at the top
of the tray hides the rest of the app, leaving only the controls on screen.

Hotkeys:

| Hotkey | Action |
|---|---|
| `Ctrl+Alt+F5` | Show or hide the overlay |
| `Ctrl+Alt+F6` | Zoom in |
| `Ctrl+Alt+F7` | Zoom out |
| `Ctrl+Alt+F8` | Keep the player centered |
| `Ctrl+Alt+F9` | Switch the mouse between the game and the overlay (a border shows when the mouse is on the overlay) |

The overlay remembers its map controls, such as the zoom and which markers are shown, between
sessions.

## Settings

Open **Settings** at the bottom of the window. Changes take effect when you click **Save**.

**Language** changes right away, with no need to click **Save**. The app follows your Windows language by default, and supports the same 18 languages as the map.

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

Settings are saved to `%LocalAppData%\StalkerGPS\settings.json`. The app also remembers where its window
was, and reopens there on the same monitor if that monitor is still connected.

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
- Nothing is sent over the internet. The app only downloads the map's public data (the IDs of
  doors and items) from stalker.vodka. It accepts connections only from your own PC, and only from the stalker.vodka map
  page; other websites are refused.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet run --project StalkerGPS
```

## For map developers

The app runs a WebSocket server at `ws://localhost:41347` (configurable). It accepts a connection
only when the `Origin` header is exactly `https://stalker.vodka` or `http://localhost:3000`
(development); anything else gets `403 Forbidden`.

Messages are JSON text frames. The current language, status, position, doors, spawns, stashes, artifacts, objectives and barricades are sent
right after connecting; after that, a message is sent when its contents change.

```json
{"type":"status","status":"tracking"}
{"type":"position","x":236349.65,"y":157956.05,"z":1116.08,"yaw":315.59}
{"type":"doors","doors":[{"guid":"C1CC15FF421A7A4831C77D8833AA4242","locked":false}, ...]}
{"type":"spawns","spawns":[{"id":"54780EF6447105E25DB460B6D6DF55A9","collected":true}, ...]}
{"type":"stashes","stashes":[{"guid":"0B3C1AC54E81E76539977992E73A5EF6","marked":true,"looted":false}, ...]}
{"type":"artifacts","artifacts":[{"id":"GArtifactWrenched","x":434943,"y":528939,"z":123}]}
{"type":"objectives","objectives":[{"id":"SQ03_Tonnel","journalQuest":"SQ03","markers":[{"x":398460,"y":593520,"z":220}]}],"targets":[{"x":419295,"y":526079,"z":1734,"objectives":[{"id":"E02_MQ02_Rihter","journalQuest":"E02_MQ02"}]}]}
{"type":"barricades","barricades":[{"id":"5011D21546A2E526C267108CB219D13F","planks":6,"destroyed":6,"cleared":true}, ...]}
{"type":"language","code":"de"}
```

- `language` is the language the app is showing, sent first and again whenever it changes. `code` is
  one of the map's language codes. It is the language actually in use, so never `auto`.
- `status` is one of `tracking`, `waitingForGame`, `locating`, `noPlayer`, `error`, `stopped`.
- `x`, `y`, `z` are game (Unreal Engine) units, in centimeters.
- `yaw` is the camera heading in degrees, 0 to 360: 0 faces +X and 90 faces +Y (clockwise seen from
  above). It's `null` when unknown.
- `doors` lists the lock state of the map's doors, keyed by the same `guid` as in the map's lock
  data (`kind` `"door"`). It also lists devices a quest switches on, such as bunker doors and
  levers, as unlocked once they're on (only some devices are in the game's memory).
- `spawns` lists whether the map's world spawns (items lying in the world) have been picked up,
  keyed by the same `id` as the items' `foundAt` entries.
- `stashes` lists the map's stashes, keyed by the stash's GUID, read from a `guid` (or `id`) field
  in the map's stash data. `marked` is whether the stash is shown on the player's in-game map: it
  becomes true once the player learns of it and false again once it's looted. `looted` is whether
  the stash has been opened and has nothing left in it; a stash opened with items left in it isn't
  looted.
- `artifacts` lists the artifacts the game has loaded around the player right now, so it holds only
  ones nearby and is empty without a player. Each is checked every 3 seconds, and they can jump
  several meters at a time. `id` is the same `id` as in the map's artifact data. A fake artifact
  has the real one's `id`, and so does a quest artifact made from another artifact's blueprint.
  They are live objects, not the map's fixed spawns, and each message replaces the last one.
- `objectives` comes from the markers the game shows for quests, checked every 2 seconds. `objectives`
  lists the objectives with a marker at a fixed place: `id` and `journalQuest` are the same as in a
  stage in `quests.json`, and `markers` holds where the game shows it (in cm). A stage can list
  several places when the quest picks one of them, so show only the stage's markers at these
  places. A marker on a stash that has been looted is left out, as the game stops showing it.
  For a route, whose waypoints the game places all at once, only the waypoint it shows is listed.
  If several objectives share a location, each is listed. `targets` lists the
  quest markers that follow something, usually NPCs, so they move. Each target's `objectives` says
  what it's for. That's one stage once its quest is known, which the app works out from the other
  markers of that quest. Until then it's every stage that follows the object, from `markerTargets`
  in `quests.json`, and it's empty if the object isn't known either. An objective with no marker isn't reported, so this isn't the full journal, and a quest can be
  active with nothing listed for it. Each message replaces the last one.
- `barricades` lists every boarded-up opening, keyed by the `barricade` ID of its opener in the map's
  lock data, with how many of its `planks` are `destroyed`. `cleared` is true once all are. Planks
  the game has no record of count as destroyed, except quest planks (`questPlanks`), which may just
  not have appeared yet, so check the count rather than assuming a cleared barricade was seen being
  broken. Each message holds the full list, so replace any earlier one. An empty list means the
  state is unknown.
- The app downloads the map's data from `assets.stalker.vodka` to know which doors, spawns and
  stashes to look for (and caches it for offline use), so ones added there are picked up the next
  time the app starts. Each doors, spawns or stashes message holds the full list, so replace any
  earlier one. They arrive
  a few seconds after a save is loaded (or a minute or two if a game update has changed how the
  game stores them and the app has to search its memory instead). An empty list means the
  state is unknown, e.g. the game was closed.
- The game drops a picked-up spawn from memory when it next starts, so a spawn the game has no
  record of is reported as collected. The exception is a spawn that only appears through a quest
  (`spawnedByQuests` isn't empty): it may just not have appeared yet, so it's left out of the list
  and should be treated as unknown. The game deletes a quest's spawn as soon as it's picked up,
  so one the app saw that then disappears is reported as collected, until the app restarts or
  another save is loaded. Doors the game has no record of are left out too.
