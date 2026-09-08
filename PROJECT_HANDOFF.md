# Railroader Minimap Server — Project Handoff

This document is written for an AI coding agent (e.g. Claude Code) picking up
this project without access to the chat history where it was built. It
captures architecture, hard-won technical discoveries, and known gotchas that
would otherwise take significant rediscovery effort. The `README.md` in this
same folder is the user-facing doc (features, install instructions); this
file is developer/agent-facing.

## What this project is

A Unity Mod Manager (UMM) mod for the game **Railroader** (Steam). It runs an
embedded HTTP + WebSocket server inside the game process, broadcasting live
track/switch/signal/car data, and serves a self-contained companion web app
(a single HTML file) that renders it as an interactive minimap. Works in a
browser on the same PC or on another device on the same network (phone/tablet
next to the monitor).

The game has a built-in minimap, but it costs frame rate by re-rendering the
world a second time. This project avoids that by reading game state directly
and drawing a lightweight 2D representation instead.

**Everything here is built on decompiled game internals (via ILSpy), not a
public modding API.** There is no official Railroader modding documentation
this relies on. A future game update could break any of it without warning.

## Current file structure

```
MinimapServerCore.cs      -- all actual logic (MonoBehaviour): server, game-state reading,
                              Harmony patch, WebSocket/HTTP handling, console command
UnityModManagerEntry.cs   -- thin UMM-specific entry point: wires logging, calls
                              Load()/OnToggle(), attaches MinimapServerCore to a GameObject
Log.cs                    -- tiny logging abstraction (Action<string> handlers) so the
                              core file has zero direct dependency on any loader's logging API
DataModels.cs             -- all DTOs (plain C# classes) sent over the WebSocket as JSON
TrackExtractor.cs         -- extracts static track/switch/signal geometry from Graph.Shared
CompanionApp.html         -- the entire client: HTML+CSS+JS in one file, embedded into the
                              DLL as a resource, served by the mod's own HTTP listener
RailroaderMinimap.csproj  -- single project file (see "Build" below)
Info.json                 -- UMM manifest
README.md                 -- user-facing docs
LICENSE                   -- MIT, this project's own code
THIRD_PARTY_LICENSES.md   -- MIT text for the bundled websocket-sharp dependency
```

Historical note: this was originally a BepInEx plugin, later ported to
support both BepInEx and UMM simultaneously via a shared "Core" class plus
two thin per-loader entry points, then **BepInEx support was dropped
entirely** (the user chose to focus solely on UMM after fighting several
loader-interop build issues -- see "Resolved gotchas" below). The
`MinimapServerCore`/entry-point split was kept even after dropping BepInEx,
since it's good separation of concerns regardless of loader count.

## Architecture

- **`MinimapServerCore`** is a plain `MonoBehaviour` (not tied to any loader
  base class). `UnityModManagerEntry.Load()` wires up `Log`'s handlers to
  UMM's `modEntry.Logger`, then calls `gameObject.AddComponent<MinimapServerCore>()`
  (or creates a new GameObject for it). `UnityModManagerEntry.OnToggle()`
  handles UMM's runtime enable/disable by creating/destroying that GameObject.
- **Two servers on two ports**: an HTTP listener (`System.Net.HttpListener`,
  port 8080) serves the static `CompanionApp.html`; a separate
  `WebSocketSharp.Server.WebSocketServer` (port 8081) handles the `/ws`
  live-data feed. They're split because WebSocketSharp's combined
  `HttpServer` class has a known bug (sta/websocket-sharp#551) where static
  file responses come back empty.
- **Track/switch/signal data** is static -- extracted once via
  `TrackExtractor`, cached as JSON, and re-broadcast only when it changes
  (Graph rebuild, or an ABS/CTC mode switch). Live updates for individual
  switches/signals push via small targeted messages instead of re-sending
  the whole network.
- **Car data** is dynamic -- gathered fresh every broadcast tick (~10Hz) from
  `TrainController.Shared.Cars`.
- **Message protocol**: every WebSocket message is JSON with a `type` field.
  Server -> client: `track_network`, `live_state`, `switch_state`,
  `signal_aspect`. Client -> server: plain strings, `GET_TRACK_DATA` and
  `SET_SWITCH:<id>`.

## Key game APIs discovered (expensive to rediscover -- decompiled via ILSpy)

These came from decompiling the game's own assemblies. If something breaks
after a game update, these are the exact APIs to re-verify first.

- `Graph.Shared` -- `.Segments`, `.Nodes`, `.IsSwitch(node)`, `.GetNode(id)`,
  `.HasPopulatedCollections`. The authoritative, already-maintained
  collections for track topology -- never scene-scan for this.
- `TrainController.Shared.Cars` -- the authoritative car list. Scene-scanning
  via `FindObjectsOfType<Car>()` **does not work** -- Car GameObjects are
  inactive when chunk-streamed out, and `FindObjectsOfType` (without
  `includeInactive: true`) misses them silently.
- `Car.GetCenterPosition(Graph)` / `Car.GetCenterRotation(Graph)` -- the
  car's true geometric center/rotation. **`Car.LocationF`/`Car.LocationR` are
  NOT the center** -- they're the front/rear axle reference points. This was
  a real, shipped bug: using `Graph.GetPositionRotation(car.LocationF)`
  directly (treating it as center) caused closely-coupled cars to render
  with visibly overlapping icons. Always use `GetCenterPosition`/
  `GetCenterRotation` for anything that should represent "where the car is."
- `Car.GetPositionFR(Graph, out posF, out posR)` -- front/rear axle
  positions; `Vector3.Distance(posF, posR)` gives a real per-car length
  (used for proportional icon sizing client-side, since it's shorter than
  true car length -- axles are inset from the ends -- a fudge factor of
  ~1.35 is applied client-side to approximate full length).
- `Car.CoupledTo(Car.LogicalEnd end)` -- direct O(1) lookup of the car
  coupled at a given end (`Car.LogicalEnd` is **nested inside `Car`**, not a
  top-level type -- must be qualified as `Car.LogicalEnd.A`/`.B`). Used to
  find a locomotive's coupled tender (check both ends, since orientation
  varies) so fuel/water can be surfaced in the engine's own popup.
- A Harmony postfix patch on `Car.UpdateMapIconPosition` captures the same
  live position the game's own built-in minimap uses, as a fallback source
  when the Graph-based center computation isn't available.
- `WorldTransformer.WorldToGame(Vector3)` / `.GameToWorld(Vector3)` -- the
  coordinate conversion between true Unity world space and the game's
  internal "game space" (which is what track/segment positions use). Car
  positions captured via the Harmony patch come back in world space and must
  be converted back with `WorldToGame`.
- `MapCoordinates.MapZ(z)` = `-z` -- Z-axis flip needed for north-up
  orientation matching the base game's own map.
- `TrackNode.isThrown` (lowercase i), `TrackNode.OnDidChangeThrown` (event),
  `TrackNode.IsCTCSwitch` -- switch state and CTC-controlled-switch flag.
- `Track.Signals` namespace (a whole CTC/signaling subsystem): `CTCSignal`
  (abstract; `.CurrentAspect`, `.id`, `.direction`), `SignalAspect` (enum:
  `Stop, Approach, Clear, DivergingApproach, DivergingClear, Restricting`),
  `SignalStorage` (the actual data store -- `.GetSignalAspect(id)`,
  `.ObserveSignalAspect(id, callback)`, `.GetBlockOccupied(id)`,
  `.ObserveSystemMode(callback)` for ABS/CTC mode changes), `CTCPanelController`
  (`.Shared` static singleton -- reach `SignalStorage` via
  `CTCPanelController.Shared.GetComponentInParent<SignalStorage>()`, since
  `SignalStorage` itself has no `.Shared`).
- `StateManager.ApplyLocal(new RequestSetSwitch(nodeId, !isThrown))` (namespace
  `Game.Messages`) -- the exact message the game's own switch-marker UI sends
  for a plain click. Used for the tap-to-throw-switch feature. CTC-controlled
  switches are refused server-side before this is ever called.
- `Car.Definition.LoadSlots`, `Car.GetLoadInfo(slot)` (extension method
  returning `CarLoadInfo? { LoadId, Quantity }`), `CarPrototypeLibrary.instance
  .LoadForId(loadId)` -> `Load` (`.QuantityString(quantity)` gives an
  already-formatted string like `"50,000 lbs Coal"`).
- `Car.Waybill` -> `OpsController.Shared.AreaForCarPosition(destination)` ->
  `Area.tagColor` -- the exact color the base game's own Tab-key destination
  overlay uses. Freight cars use this; engines/tenders/passenger cars
  intentionally don't (matches base game convention).
- `UI.Console.Console` (namespace `UI.Console` -- **must be fully qualified**,
  its bare name collides with `System.Console`). `.shared` static accessor,
  `.AddLine(string)` to print output, `event Action<string> OnUserInput`
  fires for every line typed -- **not a registered-command system**, just a
  raw text firehose any listener can react to. Used for the in-game
  `minimap`/`minimap_url` console command (also accepts an optional leading
  `/`, since it wasn't confirmed either way whether the game's own console
  convention expects one).

## Resolved gotchas (do not reintroduce these)

1. **Unity's `?.` operator does not respect Unity's destroyed-object
   override.** Unity overrides `==`/`!=` so a destroyed `UnityEngine.Object`
   compares as null, but `?.` uses a raw reference check that bypasses this.
   `CTCPanelController.Shared?.GetComponentInParent<T>()` threw
   `NullReferenceException` deep inside `GetComponentInParent` when `Shared`
   held a stale reference to a destroyed instance from a previous save
   (`CTCPanelController` has no `OnDestroy` that nulls out `Shared`). Always
   use `if (x == null) return;` before calling anything on a resolved Unity
   singleton, never `x?.Method()`.

2. **An uncaught exception in `Update()` can silently stop Unity from calling
   it again.** `Update()` now wraps its entire body (`UpdateInternal()`) in a
   top-level try/catch that logs and continues, specifically because gotcha
   #1 above caused exactly this failure mode once already.

3. **XML comments in `.csproj` files cannot contain `--`.** This caused
   repeated `MSB4025` parse failures during development. Any comment
   mentioning something like "do X -- because Y" needs a different
   separator (colon, em dash character, parentheses).

4. **Two `.csproj` files with the same `AssemblyName` in the same folder
   will corrupt each other's `obj`/`bin` build state** if not explicitly
   isolated. This came up during the (now-abandoned) dual BepInEx+UMM build
   setup. Setting `BaseIntermediateOutputPath`/`BaseOutputPath` *inside* an
   SDK-style `.csproj` doesn't reliably work either -- MSBuild's SDK.props
   import reads those properties before the project's own `PropertyGroup`
   runs (there's an explicit `MSB3539` warning for this). If this situation
   ever recurs, pass `-p:BaseIntermediateOutputPath=... -p:BaseOutputPath=...`
   on the command line instead, or put the projects in separate folders
   entirely (the cleaner fix). N/A currently since there's only one project.

5. **BepInEx's and UMM's bundled Harmony versions are different and
   genuinely incompatible on some APIs.** BepInEx's newer Harmony marks
   `Harmony.UnpatchAll(string)` obsolete-as-error, recommending
   `UnpatchSelf()`; UMM's older bundled Harmony doesn't have `UnpatchSelf()`
   at all. Currently moot (UMM-only now), but if BepInEx support is ever
   re-added, this needs a preprocessor split or reflection-based call again.

6. **`MSBuild` node reuse can cache stale state across different project
   files built back-to-back in the same session** (`dotnet build`'s
   background worker processes). Symptom: alternating between two different
   project files, the *second* one in a session intermittently fails in ways
   a fresh process wouldn't. `dotnet build-server shutdown` or
   `/nodeReuse:false` forces a clean process. Not currently relevant with a
   single project, but worth remembering if a second build target is ever
   added.

7. **A UMM GitHub issue (newman55/unity-mod-manager#133) specifically
   discusses Railroader mods with more than one DLL** (this mod ships
   `WebSocketSharp.dll` alongside the main assembly) **failing to load
   depending on UMM's install method.** Confirmed working on **DoorstopProxy**
   for this project. If a user reports the mod not loading at all under UMM,
   ask which install method they're on first.

## Feature list (roughly build order)

1. Live track geometry, switch state (color-coded ground-throw icon), car
   positions/types, pan/zoom/touch companion app.
2. Destination-based freight coloring (matches base game's own system).
3. Elongated, zoom-proportional car icons (later found to need the
   center-position fix above to actually space correctly on curves).
4. Real CTC signals: six-aspect signal mast icon, three lamp positions
   (top=clear, middle=approach, bottom=stop/restricting), pushed live.
5. Tap-to-throw for non-CTC switches (CTC switches remain read-only, matching
   real game behavior -- they're locked out at the track).
6. Cargo/load display in car popups, including a locomotive's coupled
   tender's fuel/water.
7. UMM port (after a period of supporting both loaders, then dropping
   BepInEx entirely).
8. In-game console command (`minimap`/`minimap_url`) printing connection URLs.
9. Client-side "Text Size" slider (100-200%) scaling popups/legend (CSS
   variable) and canvas-drawn train labels (JS font-size multiplier),
   persisted via `localStorage`.

## Known open items / not yet done

- **No authentication on the server at all.** Anyone on the local network can
  view live state and (since switch-throwing was added) actually control the
  game. Documented as a known limitation in the README. A config toggle to
  disable remote switch control while keeping the read-only minimap would be
  a reasonable addition; not yet implemented.
- **Multiplayer is only partially confirmed.** A non-host client successfully
  threw a switch in a real multiplayer session (confirms `RequestSetSwitch`
  round-trips correctly through the game's networking even from a non-host).
  Other features (CTC/signal reads, cargo, live tracking) haven't been
  specifically exercised from a non-host client yet, though there's good
  reason to expect they work the same way (the underlying `KeyValueObject`
  system they read from appears to be a general networked-state layer).
- **The axle-to-full-length fudge factor (1.35) for per-car icon sizing is an
  estimate, not verified against actual car model dimensions.** May need
  visual tuning.
- **`ModEntry.Logger.Warning(...)`'s existence was inferred by convention**,
  not directly confirmed in UMM's public docs/examples (only `.Log()` and
  `.Error()` were directly confirmed) -- it did compile successfully, so this
  is resolved in practice, just noting the provenance.

## Build

Single project, single DLL:

```powershell
Remove-Item -Recurse -Force obj, bin   # if anything seems stale
dotnet build RailroaderMinimap.csproj -c Release
```

Output: `bin\Release\net48\RailroaderMinimapServer.dll`. Install alongside
`WebSocketSharp.dll` and `Info.json` in a `RailroaderMinimapServer` folder
under UMM's `Mods` directory.

The `.csproj` has hardcoded `HintPath`s for the game's own assemblies and for
UMM/Harmony, both under a `$(RailroaderDir)` MSBuild property near the top of
the file -- update that one property if the game is installed somewhere
other than the default Steam path.
