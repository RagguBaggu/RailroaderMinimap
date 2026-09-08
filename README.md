# Railroader Minimap Server

A [Unity Mod Manager](https://www.nexusmods.com/site/mods/21) mod for [Railroader](https://store.steampowered.com/app/1683150/Railroader/) that broadcasts live track, switch, signal, and rolling-stock data over a local WebSocket, plus a self-hosted companion web app that renders it as an interactive minimap — on the same PC, or on a phone/tablet next to your monitor.

Unlike the game's built-in minimap, this doesn't render the world a second time to draw it, so it doesn't cost you frame rate as you zoom out or as traffic in a yard grows.

## Features

- **Live track geometry** — every segment, sampled at even intervals along the actual curve
- **Live switch state**, rendered as a ground-throw stand icon (green = normal, red = reversed), pushed instantly when a switch is thrown rather than polled
- **Tap-to-throw** — tap any non-CTC switch on the map to flip it directly, the same as a plain click on it in-game. CTC-controlled switches are locked out at the track (matching real game behavior) and show their details instead
- **Real CTC signals** — rendered as a proper signal mast with three lamp heads; the lamp matching the current aspect (Stop/Approach/Clear/Diverging.../Restricting) lights up in the correct color, the other two stay dark, just like a real signal head
- **Live car/engine tracking** — every car in the world, including ones whose visual model is currently unloaded (far from the player, mid chunk-load), using the same position source the game's own minimap uses
- **Destination-based freight coloring** — freight cars are colored using the exact same `Area.tagColor` the base game uses for its own Tab-key destination overlay, vivid while en route and dimmed once arrived. Passenger cars, engines, and tenders render as a plain neutral color instead, matching the base game's own convention of not destination-coloring them
- **Cargo details** — tap any car to see what it's carrying and how much, using the game's own formatted quantity strings (e.g. "50,000 lbs Coal")
- **Tap-for-details** on any car, switch, or signal; a "Pin Engine Info" toggle keeps every locomotive's info panel visible at once
- **Automatic refresh** — the track cache invalidates and re-broadcasts itself automatically when the track network changes (new track unlocked via milestones, added infrastructure, an ABS/CTC mode switch, etc.) — no manual refresh needed for normal play
- **No executable required** — the companion app is a plain web page, served directly by the mod itself. Open a URL, nothing to download or run
- **Same-PC or cross-device** — works in a browser tab on the same machine, or from any phone/tablet on your home network
- **Touch-friendly** — pinch to zoom, drag to pan, tap for details, on top of scroll-wheel/click support for desktop

## Requirements

- Railroader (Steam)
- [Unity Mod Manager](https://www.nexusmods.com/site/mods/21), installed and confirmed working with Railroader (i.e. you can already see it launch alongside the game)

## Installation

1. **Install Unity Mod Manager**, if you don't already have it, and confirm it's working with Railroader before proceeding — install it, launch the game once, and check that UMM's own installer/log shows Railroader as a recognized, working target.

2. **Create a mod folder** named `RailroaderMinimapServer` inside UMM's `Mods` folder (find this via the UMM installer's own UI, or wherever your other installed UMM mods for Railroader already live).

3. **Copy these files** into that folder:
   - `RailroaderMinimapServer.dll`
   - `WebSocketSharp.dll` (sits alongside the main DLL in your build output)
   - `Info.json`

4. **Launch Railroader**, open UMM's mod menu in-game, and confirm "Railroader Minimap Server" shows up and is enabled. Check UMM's log (via its own UI's Log tab, or `Railroader_Data\Managed\UnityModManager\Log.txt`) for:

   ```
   Minimap server ready. Open one of these in a browser:
     http://localhost:8080/   (this PC)
     http://192.168.x.x:8080/   (from another device on this network)
   ```

## Usage

- **On the same PC:** open `http://localhost:8080/` in any browser.
- **On another device** (phone, tablet, laptop) on the same WiFi/network: open `http://<the-LAN-address-from-the-log>:8080/`.

The page connects automatically — there's nothing to type in, no pairing step. It also auto-reconnects if the connection drops (e.g. the game restarts).

**Controls:**

| Action | Desktop | Mobile |
|---|---|---|
| Zoom | Scroll wheel, or Zoom In/Out buttons | Pinch |
| Pan | Click and drag | One-finger drag |
| View details | Tap/click a car, switch, or signal | same |
| **Throw a switch** | **Tap/click a non-CTC switch** | **same** |
| Pin every engine's info | "Pin Engine Info" button | same |
| Reset view | "Reset View" button | same |
| Fix map orientation | "Flip X" / "Flip Z" buttons | same |

The "Diagnostics" button reveals a message log and live stats (segment/switch/signal/car counts, message rate) — hidden by default for a cleaner view, useful for troubleshooting.

### Icon legend

- **White wedge** — engine or tender
- **White rectangle** — passenger car
- **Colored rectangle** — freight car, colored by its waybill destination (vivid = en route, dim = arrived); plain gray if it has no active destination
- **Circular stand icon** — switch (green = normal, red = reversed); labeled "CTC" underneath if it's signal-controlled and locked out at the track
- **Signal mast** — top lamp lit = Clear, middle = Approach, bottom = Stop/Restricting (diverging aspects share the same lamp/color)

## Firewall setup (for cross-device use)

Loopback traffic (same-PC access via `localhost`) always bypasses Windows Firewall, so same-PC use works out of the box. **Reaching the server from another device requires opening two ports:**

```powershell
New-NetFirewallRule -DisplayName "Railroader Minimap HTTP" -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow
New-NetFirewallRule -DisplayName "Railroader Minimap WS"   -Direction Inbound -Protocol TCP -LocalPort 8081 -Action Allow
```

(Two ports because the HTTP page and the WebSocket feed run on separate listeners — see [Why two ports?](#why-two-ports) below.)

### If another device still can't connect after that

This tripped us up hard during development, so it's worth checking in order:

1. **Windows network profile.** Run `Get-NetConnectionProfile`. If your WiFi network shows `NetworkCategory : Public`, other devices may not be able to discover or connect to your PC **even with the firewall rules above and even with the firewall fully disabled** — the Public/Private distinction affects network visibility at a level below ordinary firewall rules. Switch it in **Settings → Network & Internet → Wi-Fi → [your network] → Network profile type → Private**.

2. **Router-level WiFi isolation.** Look for "AP Isolation," "Client Isolation," or "Guest Network" settings in your router's admin panel. Some ISP-provided gateways (TELUS's Actiontec units, for example) have a "Smart WiFi"/band-steering feature that shows one unified network name but doesn't always bridge the 2.4GHz and 5GHz radios to each other — a phone on one band and a PC on the other can fail to see each other despite sharing a network name. If nothing else works, try splitting the gateway's WiFi into separate 2.4GHz/5GHz networks and manually connecting both devices to the same one.

3. **A free tool called [Fing](https://www.fing.com/)**, installed on the other device, is the fastest way to tell these two cases apart: if it can't even see your PC in its device list, it's a network/router-level isolation issue (steps 1–2). If it *can* see the PC but the app still won't load, it's more likely still a firewall/port issue.

## Security considerations

This mod's server has **no authentication**. Anyone on your local network can connect, view your live game state, and — since the tap-to-throw-switch feature was added — **actually flip switches in your game**. For a home network this is a reasonable tradeoff for the convenience, but it's worth being deliberate about:

- Don't run this on a shared/public network (coffee shop WiFi, a LAN party, a shared apartment network) without accepting that risk.
- There's currently no way to disable remote switch control while keeping the read-only minimap features. If that's something you want, it'd be a straightforward addition (a config toggle defaulting to read-only) — just not implemented yet.

## Known limitations

- **Multiplayer is untested.** Everything here has been built and verified in single-player. Several of the underlying game APIs this mod calls into assert host-only execution internally (`StateManager.AssertIsHost()` and similar show up throughout the decompiled CTC/signal code). Behavior as a non-host client, or with multiple players each running this mod in the same session, is unknown.
- **This mod is built on decompiled internals, not a public modding API.** A future Railroader update could rename or restructure any of the game classes this relies on (`Graph`, `TrainController`, `OpsController`, `Track.Signals.*`, etc.) without warning. If the mod stops working after a game update, that's the likely cause — check for an updated release before assuming something else is wrong.
- **UMM's install method matters for this mod specifically**, since it ships two DLLs (the mod itself plus `WebSocketSharp.dll`). This has been tested and confirmed working on the **DoorstopProxy** install method. A [UMM GitHub issue](https://github.com/newman55/unity-mod-manager/issues/133) specifically discusses Railroader mods with more than one DLL not loading correctly under the Assembly injection method — if you're on Assembly injection and the mod doesn't start, try copying `WebSocketSharp.dll` directly into `Railroader_Data\Managed\` instead of the mod's own folder.
- The companion app's connection URL uses `location.hostname` from the page it's served on — if you're doing something unusual with DNS or a reverse proxy in front of this, you may need to adjust `CompanionApp.html` directly.
- Custom maps (if added via FUSE in the future) may use a different world-orientation convention than the base game map — the Flip X/Flip Z buttons exist specifically as a manual override if a map ever renders mirrored.

## Technical notes

For anyone extending this mod or just curious how it works:

- **Track/switch data** comes from `Graph.Shared` (`Segments`, `Nodes`, `IsSwitch`), not scene scans. Switch push-updates are wired to `TrackNode.OnDidChangeThrown`. The whole cache invalidates and re-broadcasts automatically via `Graph`'s own `GraphDidRebuildCollections` message, and separately via `SignalStorage.ObserveSystemMode` (an ABS/CTC mode switch can change a switch's CTC status without triggering a graph rebuild). Both paths are throttled to at most one rebuild-and-broadcast per second, since a bulk operation can trigger dozens of invalidations in a single frame.
- **Car positions** come from `TrainController.Shared.Cars`, with a Harmony postfix patch on `Car.UpdateMapIconPosition` capturing the same live position the game's own minimap uses — reliable regardless of whether a car's visual model is currently loaded. A `Graph`/`Location`-based lookup is preferred as the primary source instead (recomputed fresh every call, immune to the staleness a cached position can develop after a floating-origin rebase), with the Harmony-captured value as fallback.
- **Switch throwing** sends `StateManager.ApplyLocal(new RequestSetSwitch(nodeId, !isThrown))` — the exact message the game's own switch-marker UI sends for a plain click. CTC-controlled switches are refused server-side, not just hidden client-side.
- **Destination colors** for freight cars come from `Car.Waybill.Value.Destination` → `OpsController.Shared.AreaForCarPosition(...)` → `Area.tagColor`, with the same en-route/arrived brightness adjustment (HSV boost vs. dimming) found in the base game's own destination-coloring logic.
- **CTC signals** come from `FindObjectsOfType<CTCSignal>()` (active-only — an inactive signal here means "not unlocked this session," unlike cars where inactive means "streamed out"). Aspect comes from `CTCSignal.CurrentAspect`; live changes push via `SignalStorage.ObserveSignalAspect`, reached through `CTCPanelController.Shared.GetComponentInParent<SignalStorage>()`.
- **Cargo info** comes from `Car.GetLoadInfo(slot)` for each of `Car.Definition.LoadSlots`, resolved to a `Load` via `CarPrototypeLibrary.instance.LoadForId(...)`, formatted with the game's own `Load.QuantityString(quantity)`.
- **Coordinate system**: track/switch positions are in the game's "game space" coordinate system (`transform.localPosition`-based for track, `WorldTransformer.WorldToGame(transform.position)` for signals). Car positions from `UpdateMapIconPosition` come back in true world space via an internal `WorldTransformer.GameToWorld` call, so they're converted back with `WorldTransformer.WorldToGame` before being sent out. Everything is then Z-flipped to match the base game map's north-up orientation.
- <a id="why-two-ports"></a>**Why two ports?** WebSocketSharp's combined `HttpServer` class (which can serve both static files and WebSocket connections from one listener) has a known, unresolved upstream bug ([sta/websocket-sharp#551](https://github.com/sta/websocket-sharp/issues/551)) where static-content responses come back empty in the browser. The companion page is served by a separate, plain `System.Net.HttpListener` instead, while WebSocketSharp's `WebSocketServer` (a completely different, working code path in the same library) continues to handle the `/ws` endpoint.
- **A Unity `?.` gotcha worth knowing about**: Unity overrides `==`/`!=` on its objects so a destroyed object compares as `null`, but the `?.` null-conditional operator bypasses that override. Code in this project that resolves game singletons uses direct `== null` checks rather than `?.` for exactly this reason — a stale static reference to a destroyed object (e.g. `CTCPanelController.Shared` across a save switch) can otherwise throw `NullReferenceException` deep inside a Unity method call instead of being caught by the null check that looks like it should have caught it.
- **Message protocol** — every WebSocket message is JSON with a `type` field: `track_network` (segments, switches, and signals, sent on connect or on request), `live_state` (car positions, ~10Hz), `switch_state` (pushed on individual switch throws), `signal_aspect` (pushed on individual signal aspect changes). The client can send `GET_TRACK_DATA` (force a fresh track/switch/signal rebuild) or `SET_SWITCH:<id>` (throw a non-CTC switch).

## License

This project's own code is licensed under the [MIT License](LICENSE).

It bundles [websocket-sharp](https://github.com/sta/websocket-sharp) (MIT License) as a NuGet dependency — see [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md) for its full license text.

## Acknowledgments

- [Unity Mod Manager](https://www.nexusmods.com/site/mods/21) and [HarmonyLib](https://github.com/pardeike/Harmony) (bundled with UMM), which make this whole approach possible
- [websocket-sharp](https://github.com/sta/websocket-sharp), the only working way to run a WebSocket server under Unity's Mono runtime (see the Technical Notes above for why)
- **Map Enhancer**, a separate community mod for Railroader, was an invaluable reference during development. Studying its decompiled source directly led to two of this mod's core techniques:
  - Enumerating cars via `TrainController.Shared.Cars` combined with a Harmony patch on `Car.UpdateMapIconPosition`, rather than an unreliable scene scan
  - The entire destination-based freight coloring system, traced from its `TraincarColorUpdater` coroutine back to `OpsController`/`Area.tagColor`

  Credit to Map Enhancer's author for figuring these out first — this project's implementation is its own, built from first principles once the right game APIs were identified, but the discovery process leaned directly on that mod's prior work.
