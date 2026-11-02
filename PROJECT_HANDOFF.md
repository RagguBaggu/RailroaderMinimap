# Railroader Minimap Server — Project Handoff

This document is written for an AI coding agent (e.g. Claude Code) picking up
this project without access to the chat history where it was built. It
captures architecture, hard-won technical discoveries, and known gotchas that
would otherwise take significant rediscovery effort. The `README.md` in this
same folder is the user-facing doc (features, install instructions); this
file is developer/agent-facing.

## What this project is

A mod for the game **Railroader** (Steam), loaded by the game's own built-in
mod loader (Railroader 2026.1+, the "Maintenance of Way" update). It runs an
embedded HTTP + WebSocket server inside the game process, broadcasting live
track/switch/signal/car data, and serves a self-contained companion web app
(a single HTML file) that renders it as an interactive minimap. Works in a
browser on the same PC or on another device on the same network (phone/tablet
next to the monitor).

The game has a built-in minimap, but it costs frame rate by re-rendering the
world a second time. This project avoids that by reading game state directly
and drawing a lightweight 2D representation instead.

**Everything here is built on decompiled game internals (via ILSpy), not a
public modding API.** Since 2026.1 the game has an official mod *loader*
(`Railroader.Modding.dll`: an entry-point interface, world lifecycle hooks,
and a logger -- that's all of it), but every piece of game data this mod
reads is still internal API. A future game update could break any of it
without warning; 2026.1 itself broke a lot (see "Railroader 2026.1
migration" below).

## Current file structure

```
MinimapServerCore.cs      -- all actual logic (MonoBehaviour): server, game-state reading,
                              MoW track condition, WebSocket/HTTP handling, console command
GameModEntry.cs           -- thin entry point for the game's built-in loader
                              (IModEntryPoint + IWorldLifecycle): wires logging,
                              attaches MinimapServerCore to a GameObject, forwards
                              world load/unload
Log.cs                    -- tiny logging abstraction (Action<string> handlers) so the
                              core file has zero direct dependency on any loader's logging API
DataModels.cs             -- all DTOs (plain C# classes) sent over the WebSocket as JSON
TrackExtractor.cs         -- extracts static track/switch/signal geometry from Graph.Shared
WaypointQueueBridge.cs    -- reflection-only soft dependency on the third-party "Waypoint
                              Queue" mod, so this project has no compile-time dependency
                              on it (see "Key game APIs discovered" below)
CompanionApp.html         -- the entire client: HTML+CSS+JS in one file, embedded into the
                              DLL as a resource, served by the mod's own HTTP listener
RailroaderMinimap.csproj  -- single project file (see "Build" below)
Info.json                 -- the game's asset-pack manifest (identifier, scope,
                              "assemblies" list naming the DLL to load)
README.md                 -- user-facing docs
LICENSE                   -- MIT, this project's own code
```

Historical note: this was originally a BepInEx plugin, later ported to
support both BepInEx and UMM simultaneously via a shared "Core" class plus
two thin per-loader entry points, then **BepInEx support was dropped
entirely** (the user chose to focus solely on UMM after fighting several
loader-interop build issues -- see "Resolved gotchas" below). The
`MinimapServerCore`/entry-point split was kept even after dropping BepInEx,
since it's good separation of concerns regardless of loader count. In 0.10.0
**UMM was dropped too**, in favor of the game's own built-in loader (the user
chose built-in-only over supporting both). 0.9.0 is the last UMM release and
the last one that works on pre-2026.1 game versions.

## Architecture

- **`MinimapServerCore`** is a plain `MonoBehaviour` (not tied to any loader
  base class). `GameModEntry.OnModLoad()` wires up `Log`'s handlers to the
  game's `IModContext.Log`, then creates a `DontDestroyOnLoad` GameObject and
  adds `MinimapServerCore` to it. `OnModUnload()` tears it down with
  `DestroyImmediate` (see the built-in loader notes below for why not
  `Destroy`). `OnWorldLoad`/`OnWorldUnload` (`IWorldLifecycle`) forward to the
  core, which drops every reference into the old world on unload.
- **Two servers on two ports**: an HTTP listener (`System.Net.HttpListener`,
  port 8080) serves the static `CompanionApp.html`; a separate
  `WebSocketSharp.Server.WebSocketServer` (port 8081) handles the `/ws`
  live-data feed. They're split because WebSocketSharp's combined
  `HttpServer` class has a known bug (sta/websocket-sharp#551) where static
  file responses come back empty. WebSocketSharp is the copy the game itself
  ships in `Managed/WebSocketSharp.dll` since 2026.1 (assembly name
  `WebSocketSharp`, unlike the NuGet package's `websocket-sharp`), referenced
  with `Private=False` -- so the mod is one DLL with no dependencies to
  bundle. That build's `AddWebSocketService<T>` takes an `Action<T>`
  initializer for a `new()`-constructed behavior, not a factory `Func<T>`.
- **Track/switch/signal data** is static -- extracted once via
  `TrackExtractor`, cached as JSON, and re-broadcast only when it changes
  (Graph rebuild, or an ABS/CTC mode switch). Live updates for individual
  switches/signals push via small targeted messages instead of re-sending
  the whole network.
- **Car data** is dynamic -- gathered fresh every broadcast tick (~10Hz) from
  `TrainController.Shared.Cars`.
- **Track condition (Maintenance of Way)** is semi-static: polled every 5 s
  (`BroadcastTrackCondition`), only changed segments sent. A full snapshot
  always follows every `track_network` broadcast, since it's keyed by
  segment id and meaningless without the matching network.
- **Message protocol**: every WebSocket message is JSON with a `type` field.
  Server -> client: `track_network`, `live_state`, `switch_state`,
  `signal_aspect`, `track_condition`. Client -> server: plain strings,
  `GET_TRACK_DATA`, `SET_SWITCH:<id>`, `WARP_CAMERA:<x>,<z>`.

## Railroader 2026.1 migration (what changed, and where things went)

Found by decompiling the 2026.1 Experimental build's `Railroader.dll` in full
(`ilspycmd -p -o <dir> -r <Managed> Railroader.dll`, then grep -- much faster
than one `-t` type at a time when many things moved at once).

- **Assemblies**: nearly all game code moved from `Assembly-CSharp.dll` +
  `Definition.dll` into **`Railroader.dll`**. `Assembly-CSharp.dll` is now a
  4 KB stub with one unrelated class; `Definition.dll` is gone.
- **Compiler gotcha when re-targeting**: the first build after the reference
  swap reported only ~4 errors (unresolved `using`s, a missing attribute
  type). Roslyn skipped binding method bodies while declaration-level errors
  existed, so the real breakage (removed methods, types that stopped being
  `UnityEngine.Object`s) only appeared after fixing those. Don't trust a
  short error list after a game update until it builds clean.
- `GalaSoft.MvvmLight.Messaging.Messenger.Default` -> **`Game.Events.EventBus`**
  (`Subscribe<T>(UnityEngine.Object listener, Action<T>)`, `Unsubscribe(listener)`).
- `CarArchetype` enum and `Car.Archetype` -> **`Car.CarType`**, now a
  `Model.CarType` struct (implicit to/from string) with prefix helpers:
  `IsLocomotiveSteam` ("LS"), `IsLocomotiveDiesel` ("LD"), `IsTender` ("LT"),
  `IsCoach` ("PB"), `IsBaggage` ("CB"), `IsCaboose` ("NE"),
  `IsMaintenanceOfWay` ("MW"), `IsFreight`.
- `TrackNode` and `TrackSegment` are **plain classes now, not
  MonoBehaviours** -- no `.name`, no `.transform`. Node position is
  `TrackNode.Position`, the same graph space `TrackSegment.CreateBezier` builds
  curves from. Ids are just `.id`.
- `TrackSegment.GetExpectedSpeedLimit()` is gone -> `EffectiveSpeedLimitMph`
  (`speedLimit`, 0 meaning the railroad-wide `RailroadSpeedLimitMph`).
- `Load` is plain data now (no ScriptableObject `.name`), with namespaced ids:
  `rr/water`, `rr/coal`, `rr/diesel-fuel`, `rr/passengers`. Lookup moved from
  `CarPrototypeLibrary.instance.LoadForId` to static
  **`Model.Ops.Definition.CarLoadLibrary.GetLoad(id)`**.
  `CarLoadTargetLoader` stores the id directly as `loadId` (its `load`
  property just calls `CarLoadLibrary.GetLoad(loadId)`).
- `Model.Ops.PassengerStop` is a plain class (not findable via
  `FindObjectsOfType`) -> enumerate with `PassengerStop.FindAll()` (=
  `OpsController.Shared.AllPassengerStops`). `ProgressionDisabled` ->
  `IsProgressionEnabled`. `.name`, `.CenterPoint`, `.TrackSpans` remain.
- Console commands: `IConsoleCommand` + `[ConsoleCommand]` + the `_commands`
  dictionary are gone. See the `/minimap` entry below for the new system.
- `UnityEngine.Object.FindObjectsOfType<T>()` is deprecated in this Unity
  version -> `FindObjectsByType<T>(FindObjectsSortMode.None)` (still excludes
  inactive objects by default).
- **Harmony is not part of the game.** A `0Harmony.dll` in `Managed/` on a
  modded install was put there by another loader (Railloader / UMM -- check
  its timestamp against `Railloader.Injector.dll`). A built-in-loader mod
  can't assume it exists, which is why the `Car.UpdateMapIconPosition`
  postfix was removed (see the car-position entry below).

### The built-in mod loader

- Packs are folders under `Documents\Railroader\Mods\` (`UserPaths.
  RailroaderDirectory`, overridable with `-homedir`), plus `ModDev\` for
  writeable development packs. **Not** `<game>\Mods\` -- that's UMM's.
- Manifest is `Info.json` (`AssetPack.Common.AssetPackInfo`, parsed with
  Newtonsoft, so case-insensitive and unknown fields ignored). Required:
  `identifier` matching `domain/name` (lowercase letters, digits, `-`, `.`;
  each part starting with a letter). `scope`: `unscoped` (default; recorded
  in saves, required of multiplayer peers), `optional`, or **`client`**
  (per-player, not recorded, own list in the Mods screen -- what this mod
  uses). `dependencies.required` takes `"id [constraint]"` strings, e.g.
  `"rr/game"` or `"rr/game >=2026.1.0"`.
- `assemblies`: DLLs (paths relative to the pack) the game `Assembly.LoadFrom`s.
  **Every listed DLL must contain a public `IModEntryPoint`** or it logs an
  error and is skipped -- don't list plain dependency DLLs there (none needed
  now anyway). `GetExportedTypes()` is called on it, so every public type must
  resolve against assemblies the game has.
- DLL mods only load when Preferences > Experimental > **"Allow Unsafe DLL
  Mods"** is on (takes effect on restart).
- `AssetPackManager` can unload and reload packs mid-session
  (`ApplyModConfig`, e.g. switching to a save with a different mod set): it
  calls `OnModUnload` on the old entry point and immediately `OnModLoad` on a
  fresh instance. `Object.Destroy` is deferred to end of frame, so tearing
  down with it would leave the old server holding ports 8080/8081 while the
  new one tries to bind them -- hence `DestroyImmediate`.
- Logging: `IModContext.Log` is a Serilog logger tagged with the pack id,
  written to the Unity console -> `Player.log` in
  `%USERPROFILE%\AppData\LocalLow\Giraffe Lab LLC\Railroader\`. Messages
  are Serilog *templates*: literal `{...}` in a message is treated as a
  placeholder (rendered as-is when there's no matching property, so harmless
  in practice).

### The game's built-in HTTP server (and why this mod doesn't use it)

`HTTP.GameServer` (2026.1): off by default (Preferences > Experimental > "HTTP
Server", or `-httpserver`), port 8630, WebSocketSharp `HttpServer` with
`/api/info`, `/api/services`, and a `/ws` game-state socket. It binds
**`IPAddress.Loopback` only** and `LocalOriginPolicy` rejects any Host/Origin
that isn't `localhost`/`127.0.0.1`/`[::1]` -- so a phone or tablet on the LAN,
the main use case here, can't reach it. `RegisterHttpGetHandler`/
`RegisterWebSocketService` exist but `GameServer._httpServer` is private.
Decision (with the user): keep this mod's own 8080/8081 listeners.

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
- Fallback position when the Graph-based center computation isn't
  available (no valid `LocationF`/`LocationR` yet): `car.BodyTransform`
  (world space -> `WorldTransformer.WorldToGame`). This was a Harmony postfix
  on `Car.UpdateMapIconPosition` until 0.10.0, but that method is handed
  exactly the position the body is moved to, and Harmony isn't available to
  a built-in-loader mod (see migration notes above).
- `WorldTransformer.WorldToGame(Vector3)` / `.GameToWorld(Vector3)` -- the
  coordinate conversion between true Unity world space and the game's
  internal "game space" (which is what track/segment positions use). The
  body-transform car-position fallback is in world space and must be
  converted back with `WorldToGame`.
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
  returning `CarLoadInfo? { LoadId, Quantity }`), `CarLoadLibrary.GetLoad(
  loadId)` -> `Load` (`.QuantityString(quantity)` gives an already-formatted
  string like `"50,000 lbs Coal"`). Was `CarPrototypeLibrary.instance
  .LoadForId` before 2026.1.
- `Car.Waybill` -> `OpsController.Shared.AreaForCarPosition(destination)` ->
  `Area.tagColor` -- the exact color the base game's own Tab-key destination
  overlay uses. Freight cars use this; engines/tenders/passenger cars
  intentionally don't (matches base game convention).
- `CameraSelector.shared` (global/unnamed namespace, no `using` needed --
  confirmed by decompiling that it sits before any `namespace` block in the
  file) -- `.JumpToPoint(Vector3 gamePoint, Quaternion rotation,
  CameraIdentifier? cameraIdentifier = null)` teleports whichever camera is
  currently active (pass `null` for `cameraIdentifier` to mean "whichever
  one that is" -- it resolves to Strategy or FirstPerson itself, falling
  back to Strategy from Dispatcher mode; this project always passes `null`
  since there's no reason for the companion app to force a specific mode).
  `gamePoint` is in the same game-space (`WorldTransformer`) coordinates as
  every other position in this codebase. Internally this dispatches to
  `StrategyCameraController.JumpTo` (the free/orbit camera -- **snaps its
  own Y to the ground itself** via an internal `SnapToGround` call, so an
  inaccurate Y here is harmless for this mode) or
  `PlayerController.JumpTo` (first-person -- a direct
  `KinematicCharacterMotor.SetPositionAndRotation` teleport with **no**
  ground-snap of its own, so an inaccurate Y here would leave the character
  floating or clipped into terrain). Since this project can't know
  client-side which mode is currently active, ground height is always
  resolved server-side via `Physics.Raycast(origin, Vector3.down, ...,
  (1 << Helpers.Layers.Terrain) | (1 << Helpers.Layers.Track))` from high
  above the target X/Z (the same straight-down-raycast-onto-Terrain pattern
  the game's own track-carving tools use), so it's correct for both modes
  regardless of which one actually ends up handling the jump.
  `Physics`/`RaycastHit` needed adding a `UnityEngine.PhysicsModule` DLL
  reference to the `.csproj` -- not covered by the existing `UnityEngine`/
  `UnityEngine.CoreModule` references.
- `UI.Console.Console` (namespace `UI.Console` -- **must be fully qualified**,
  its bare name collides with `System.Console`). `.shared` static accessor,
  `.AddLine(string)` to print output. Its `event Action<string> OnUserInput`
  fires for every line typed, for any listener to react to -- this project's
  first implementation of the `/minimap` command just listened to that
  directly, which turned out to have two real bugs: typing the command
  *without* a leading `/` also got sent as an in-game chat message (the
  game's own `ConsoleCommandHandler.OnConsoleUserInput` treats any
  non-`/`-prefixed line as chat, via `StateManager.ApplyLocal(new
  Say(...))`), and typing it *with* a leading `/` still printed the game's
  own `"Command not recognized."` first, since raw event listeners can't
  "consume" an event to stop the game's own handler from also processing
  the same text. The actual, structured way to add a console command is
  `UI.Console.IConsoleCommand` (one method, `string Execute(string[]
  components)`) + `[UI.Console.ConsoleCommand("/name", "description")]` on
  the implementing class (pre-2026.1). **2026.1 replaced that** with an
  attribute-based system: a class with `[UI.Console.ConsoleCommandHandler(
  "/name", "description")]`, whose *non-public* methods carry
  `[ConsoleDefaultCommand]` (bare `/name`) or `[ConsoleSubcommand]`
  (`/name sub ...`); parameters are parsed from the typed arguments by
  `Convert.ChangeType`, and the game constructs the class itself with
  `Activator.CreateInstance` per invocation (so it reaches the server via a
  static `Instance`). `ConsoleCommandHandler.Awake` still only scans
  `Assembly.GetExecutingAssembly()` -- the game's own -- but the scan itself
  is the public `CommandProcessor.RegisterHandlers(Assembly)`. So: reflect
  `ConsoleCommandHandler`'s private `_processor` field, call
  `RegisterHandlers(typeof(...).Assembly)`, and everything else (dispatch,
  unique-prefix matching, `/help`, usage text) is the game's own code.
  Unregistering removes the key (command name without `/`) from
  `CommandProcessor`'s private `_handlers` dictionary (its value type is an
  internal struct, so it's handled as a non-generic `IDictionary`).
  `RegisterHandlers` logs an error for an already-registered name, so check
  `_handlers` first. The console object is re-checked every tick in case the
  game recreates it. The old `/minimap_url` alias was dropped: with prefix
  matching, it made `/mini` ambiguous. See `TryHookConsole` and
  `MinimapConsoleCommand` in `MinimapServerCore.cs`.
- **`UI.Map.MapLabel` is the actual source of every name shown on the base
  game's own minimap** -- `Model.Ops.Area` is NOT it (a common wrong guess,
  since `Area` looks like the obvious candidate and is otherwise useful for
  destination coloring). The base minimap is a literal top-down camera
  render of the scene (`UI.Map.MapBuilder`, with a real `mapCamera`), and
  `MapBuilder` populates its label set via
  `UnityEngine.Object.FindObjectsOfType<MapLabel>(includeInactive: true)`.
  `MapLabel` itself is trivial: just a `public string text` on a
  world-positioned `Canvas`. Confirmed by decompiling `Assembly-CSharp.dll`
  with `ilspycmd` (`dotnet tool install -g ilspycmd`; `-t <FullTypeName>
  -r <path-to-Managed-dir> Assembly-CSharp.dll` decompiles one type without
  needing a full-assembly dump) -- worth reaching for over
  `MetadataLoadContext`-based reflection (still fine for browsing type
  *signatures*) whenever actual method *bodies*/logic are what's in
  question, not just what members exist.
  - **Unlock/progression gating**: `Game.Progression.MapFeature` (found via
    the same decompile) is the milestone-unlock system. Each `MapFeature`
    has a designer-authored `gameObjectsEnableOnUnlock` array that gets
    `GameObject.SetActive(unlocked)` when the feature unlocks -- a locked
    region's `MapLabel` is (in the expected, designer-followed case)
    included in that array, so it's simply an inactive GameObject until
    unlocked. This means extracting labels via the *default*
    `FindObjectsOfType<MapLabel>()` (i.e. **not** `includeInactive: true`,
    unlike `MapBuilder`'s own call above) already excludes not-yet-unlocked
    area names for free -- same "inactive == not unlocked" convention
    already used for `CTCSignal` above, no separate progression-state check
    needed.
  - **This is also the answer for modded areas** (e.g. a mod-added industry
    like "Kirkland Coal Mine" not showing up): any mod integrating with the
    base game's own minimap adds its own `MapLabel` the same way the base
    game's own content does, at whatever position the mod places it -- so
    reading `MapLabel` instead of `Area` picks up modded regions with no
    special-casing, whereas `Area`-based extraction only ever saw base-game
    regions (and DID include locked ones, since `Model.Ops.Area` itself
    isn't what gets deactivated on lock -- only its constituent
    `Industry.ProgressionDisabled` flags and unrelated
    `gameObjectsEnableOnUnlock` entries are).
- **Locomotive supply points (water/coal/diesel) and passenger stations
  have no base-minimap icon at all** -- both are simply visible as literal
  3D geometry on the base minimap's real camera render, which our
  vector-only companion app doesn't have, so both needed a from-scratch
  data source instead of reusing an existing game marker.
  - **Water/coal/diesel: `RollingStock.CarLoadTargetLoader`.** Two earlier
    approaches were tried and abandoned before this one: (a) `MapLabel`'s
    icon-only `<sprite name="Water">` tag for water -- inconsistently
    authored across stations, so some were silently unmatched no matter
    how the text pattern was broadened, and (b) `Model.Ops.IndustryUnloader`
    (scanning `OpsController.Shared.AllIndustries` ->
    `Industry.VisibleComponents` for `.load.id` "coal"/"diesel-fuel",
    filtered to `orderLoads == false` to exclude ordinary
    industries that *receive* coal as a raw material) -- workable for
    coal/diesel but had no water equivalent at all, and needed a
    `.CenterPoint` correction (see below) to line up with the real
    structure. **`CarLoadTargetLoader` supersedes both entirely** and
    covers all three loads uniformly: a leaf `MonoBehaviour` placed
    directly at the physical crane/chute/pump, with its own `Load load`
    and a `sourceIndustry` field that's explicitly nullable ("if null,
    unlimited loads are provided") -- exactly why water (free, unlimited)
    was never reachable through the Industry-based systems above; it was
    never Industry-linked to begin with, for any of the three loads, not
    just water. **2026.1: match on `loader.loadId` (`rr/water`, `rr/coal`,
    `rr/diesel-fuel`), compared on the part after the `/`.** `Load` no
    longer has a `.name` at all. Historical, pre-2026.1: **match on
    `load.name.ToLower()` -- NOT `load.id`.**
    WaypointQueue's own code matches `load?.name?.ToLower()`; an earlier
    version of this extraction assumed `.id` would be equivalent (it works
    fine for `IndustryUnloader` elsewhere in this file) and silently
    matched zero loaders as a result -- confirmed by a temporary
    diagnostic dump of every found loader's `load.name`, which turned out
    to already read exactly `"water"`/`"coal"`/`"diesel-fuel"` (lowercase,
    hyphenated) with no `.ToLower()` even required in practice, just
    proving `.id` and `.name` are genuinely different fields here, not
    that one format was wrong. Its raw `.transform.position` is already
    exactly correct -- no `CenterPoint`-style adjustment needed.
  - **Every `CarLoadTargetLoader` in the game shares the identical
    GameObject name `"Loader"`.** Do not use `.name` as a dedup/lookup
    key for these -- an id built from it (`loader.name`, falling back to
    the load kind) collapsed all ~29 real loaders down to whichever one
    happened to be processed last once the client's `Map` (keyed by that
    id) deduplicated them, which looked exactly like "the icons aren't
    showing" with no server-side error at all. Use
    `loader.GetInstanceID()` (or similarly instance-unique data) instead.
    Confirmed non-obvious enough to cost real back-and-forth diagnosing --
    worth checking first if a similarly name-keyed marker type goes silently
    missing again in the future.
    **Found by decompiling a different mod, not the base game**: the
    WaypointQueue mod automates real Auto Engineer refueling, so its own
    fuel-stop detection logic (`RefuelService.CheckNearbyFuelLoaders`) is
    proven correct by construction -- when a base-game mechanic seems to
    have no discoverable API and another mod already implements exactly
    that mechanic, decompiling *that mod's* DLL (same `ilspycmd -r
    <Managed-dir> -r <mod's-own-dir> <mod>.dll` command, just pointed at
    the mod folder instead of `Railroader_Data\Managed`) can be more
    direct than continuing to search the base game alone.
  - **Passenger stations**: `Model.Ops.PassengerStop` is a distinct type
    from `Industry`. Since 2026.1 it's a plain class (not a component):
    enumerate via `PassengerStop.FindAll()`, unlock state via
    `.IsProgressionEnabled` (was `FindObjectsOfType<PassengerStop>()` and
    `.ProgressionDisabled`), not GameObject-active-state like
    `MapLabel`/`CTCSignal` use. Unlike `CarLoadTargetLoader`, this
    one DOES need the `.CenterPoint`-over-`.transform.position` fix below.
  - **Use `.CenterPoint`, not `.transform.position`, for `PassengerStop`
    (and any `IndustryComponent`, e.g. `IndustryUnloader`, if one gets
    used again).** Same class of bug as `Car.LocationF` vs.
    `GetCenterPosition` above -- a real, shipped bug here too, first found
    when a coal-tower marker (back when `IndustryUnloader` was still in
    use) rendered nowhere near the actual coal tower. `CenterPoint`'s
    getter prefers `trackSpans[0].GetCenterPoint()` over the raw transform
    when any track spans are populated, falling back to
    `WorldTransformer.WorldToGame(transform.position)` only when they
    aren't. **Both branches already return game-space coordinates** --
    unlike most other position reads in this codebase, do NOT wrap a
    `CenterPoint` result in another `WorldTransformer.WorldToGame` call,
    or it gets converted twice. (`CarLoadTargetLoader` is a plain leaf
    MonoBehaviour with no `CenterPoint` property at all -- its transform
    IS the real position, confirmed by WaypointQueue using it directly.)
- **`Car.CarType` is NOT a locomotive's descriptive class/model name,
  despite what the property name suggests -- it's a short category code**
  (e.g. `"LS"` for literally any steam locomotive, `"LD"` for any diesel
  one, regardless of specific class). Shipped in the Engine Info panel
  before being caught: every steam engine's "Model" field showed `"LS"`,
  every diesel's showed `"LD"`. The real display name the game's own UI
  uses (confirmed by decompiling `TagNameForCar`) is
  `Car.DefinitionInfo.Metadata.Name` (a public field chain --
  `DefinitionInfo` is `TypedContainerItem<CarDefinition>`, `.Metadata` is
  `ObjectMetadata`, `.Name` the actual string, e.g. `"P-48 Pacific"` or
  `"U30C"`), with `CarType` used only as a fallback when that's empty --
  same resolution order (`DefinitionInfo.Metadata.Name` first, `CarType`
  fallback) is now used everywhere this project reads a car's type/model,
  not just for locomotives, since ordinary freight/passenger cars likely
  have the same short-code-vs-real-name split. Separately: the game has
  no distinct "manufacturer/make" field anywhere (checked both
  `CarDefinition` and `ObjectMetadata` in `Definition.dll`) -- a name like
  "P-48 Pacific" or "U30C" is just one free-text string as far as the data
  model goes, nothing to split further.
- **Auto Engineer's single waypoint order** lives in `Model.AI`:
  `new AutoEngineerPersistence(car.KeyValueObject).Orders` (a
  `KeyValueObject`-backed struct; `Car.KeyValueObject` is a public field) ->
  `.Mode == AutoEngineerMode.Waypoint` + `.Waypoint.HasValue` -> the
  `OrderWaypoint` struct's public `LocationString` field ->
  `Graph.Shared.ResolveLocationString(locationString)` (throws
  `BadLocationException` on a stale/bad string -- wrap in try/catch) ->
  `Graph.Shared.GetPositionRotation(location).Position`, the same world-space
  convention `TrackExtractor`'s own segment sampling already uses (not
  `WorldTransformer.WorldToGame` -- `Location` is a track-graph concept, not
  a live transform). `AutoEngineerPersistence`/`Orders`/`OrderWaypoint`/
  `AutoEngineerMode` all live in `Model.AI`, a separate `using` from the
  `Model` namespace already imported for `Car`. `AutoEngineerPersistence`'s
  `Orders` property is itself backed by `KeyValueObject` (from a *separate*
  assembly, `KeyValue.Runtime.dll`, sitting alongside the others in
  `Managed/`) -- referencing this project's `Orders`/`AutoEngineerPersistence`
  types at all requires adding an explicit `<Reference Include="KeyValue.Runtime">`
  to the `.csproj`; without it, the compiler error is a slightly confusing
  "type is defined in an assembly that is not referenced" pointing at
  `KeyValueObject`, not at anything in `Model.AI` itself.
- **The third-party "Waypoint Queue" mod's per-locomotive queue** is reached
  entirely via reflection (see `WaypointQueueBridge.cs`) since its access
  point, `WaypointQueue.State.ModStateManager` (a `MonoBehaviour`), is
  `internal` to `WaypointQueue.dll` -- `Type.GetType("WaypointQueue.State.
  ModStateManager, WaypointQueue")` still resolves it despite that (assembly-
  qualified `Type.GetType` ignores accessibility). Its public static `Shared`
  property gives the live instance; its public `LocoWaypointStates` property
  is typed `IReadOnlyDictionary<string, LocoWaypointState>` -- that generic
  *interface* isn't castable to plain `System.Collections.IDictionary`, but
  the actual backing object returned (confirmed via decompiling: a plain
  `Dictionary<string, LocoWaypointState>` field) is, so casting the reflected
  value to `IDictionary` and indexing by locomotive id works without needing
  reflection for the dictionary access itself. `LocoWaypointState` and
  `ManagedWaypoint` (in `namespace WaypointQueue`, both public classes,
  unlike `ModStateManager`) expose `.Waypoints` (`List<ManagedWaypoint>`),
  and `.Name`/`.StatusLabel`/`.Location` respectively -- all public
  properties, reached via `PropertyInfo.GetValue`. One shortcut:
  `ManagedWaypoint.Location`'s declared type is `Track.Location`, a type
  from the *game's own* `Assembly-CSharp.dll` that this project already
  hard-references directly -- so the boxed return value can be unboxed with
  a plain `is Location location` pattern match, no reflection needed for
  that specific value once retrieved. Mod presence is detected once (cached
  for the process lifetime) by scanning `AppDomain.CurrentDomain.
  GetAssemblies()` for an assembly named `"WaypointQueue"` before attempting
  any of the above, so nothing in this bridge runs (or logs) when the mod
  isn't installed.
- **`ManagedWaypoint.StatusLabel` is LIVE execution state, not
  configuration** -- it defaults to `"Inactive"` and is only ever updated
  (to things like `"Running to waypoint"`, `"Refueling Coal"`) for whichever
  single waypoint in the queue is currently being executed; every other
  queued-but-not-yet-active entry just reads `"Inactive"` regardless of
  what it's actually set up to do. Initially shipped surfacing only
  `Name`+`StatusLabel` in the tap popup (a deliberate, discussed choice --
  see feature list below), which in practice showed nothing useful for any
  waypoint except the active one. Fixed by adding
  `WaypointQueueBridge.BuildActionSummary`, which reflects a wider set of
  `ManagedWaypoint`'s public properties (`CouplingSearchMode`,
  `UncouplingMode`, `WillRefuel`/`RefuelLoadName`, `WillWait`+its
  duration/time fields, `WillChangeMaxSpeed`, `StopAtWaypoint`, etc. --
  confirmed all public via decompiling) into short human-readable action
  lines describing what the waypoint will actually DO whenever it runs.
  Several of these are enum-typed, but the enums themselves are nested
  types private to `WaypointQueue.dll` with no accessible compile-time
  name -- reading the reflected value's own `.ToString()` (returns the
  member name, e.g. `"Nearest"`, `"ByCount"`) sidesteps needing one. Given
  the number of fields involved (~20), they're read via a
  `Dictionary<string, PropertyInfo>` of ManagedWaypoint's public properties
  built once, rather than one hand-declared `PropertyInfo` field per
  property.
- **Curvature** -- `Graph.Shared.CurvatureOfSegment(segment)` (public): degrees
  of curve, the max of five evenly spaced samples along the segment
  (`TrackMath.CalculateCurveDegrees` between consecutive sample points),
  each rounded to a whole byte and cached per segment id by the Graph.
  `CurvatureAtLocation(location)` gives the sample at a point.
  `TrainMath.MaximumSpeedMphForCurve(deg)` is `143 * deg^-0.57`;
  `TrainMath.CalculateMaxSpeedMph(graph, segment, equipmentCurveLimit)`
  (public extension, `Model.Physics`) is what `AutoEngineerPlanner` caps
  itself to per segment: the curve speed less max(3 mph, 20%), rounded down
  to 5 mph, at least 5, and capped at `EffectiveSpeedLimitMph`. Equipment
  curve limit is `Car.MaximumTrackCurvature`; the game's own MoW estimates
  pass 23 as a standard value, as does this mod.
- **Maintenance of Way (2026.1)** -- `Game.Mow.TrackConditionController.
  Shared` (a `GameBehaviour`; its `Shared` getter does a
  `FindAnyObjectByType` whenever the cached one is null, so don't call it
  every frame when none exists). Public per-segment getters:
  `GetCondition(seg)` (0-1, 1 = pristine), `GetConditionImpliedClass`,
  `GetGradingClass` (class it was built/maintained to, or its in-progress
  target while under construction), `GetConstructionState`,
  `GetConstructionProgressByPhase` (0-1). On the segment itself:
  `TrackSegment.PostedTrackClass` (the class actually in effect -> speed
  restriction) and `.ConstructionState` (`Track.TrackConstructionState`
  flags: 1 rails, 2 ties, 4 ballast, 8 blasting, 16 abutments, 32 bridge
  wood, 64 bridge steel, 128 portals; 0 = complete). Turntable segments
  (`!IsRightOfWay`) are always reported pristine. The useful formulas are
  all `internal` in `TrackConditionModel`, so `MinimapServerCore`/the client
  replicate them: class from condition (<=0.2 -> 1, <=0.4 -> 2, <=0.6 -> 3,
  <=0.8 -> 4, else 5); class N targets condition N*0.2; max speed per class
  10/25/40/60/80 mph; tooltip text `"Class {grading} track, {pct}%
  condition"` with pct = floor(min(condition/target,1)*100), or 100 when
  within 0.001 of target; "slow order" tie stripe when max(posted) <
  min(signed limit, max(grading)). Construction milestones now place
  unbuilt segments in the Graph, so they render (dashed) rather than being
  absent.
- **AssetRipper (`winget install AssetRipper.AssetRipper`) is fully
  scriptable via plain HTTP, not just its browser GUI** -- despite
  shipping as a Blazor-ish local web app, `POST /LoadFolder` and
  `POST /Export/UnityProject` (both `application/x-www-form-urlencoded`,
  body `path=<value>`; full spec at `/openapi.json`) do the whole
  load-then-export-a-Unity-project flow headlessly (`--headless` flag
  skips auto-opening a browser). Useful whenever a question needs actual
  binary asset data ilspycmd can't see (textures, ScriptableObject data
  like a TextMeshPro Sprite Asset's sprite name table) rather than just
  code. The exported `.asset` files are plain YAML -- e.g. `Assets/
  Resources/sprites/TMP Railroader Sprites.asset` has the game's complete
  named-sprite list (`m_SpriteCharacterTable[].m_Name`) straight in text,
  no image-parsing needed to enumerate it.

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

5. *(Moot since 0.10.0 dropped Harmony entirely.)* **BepInEx's and UMM's
   bundled Harmony versions are different and genuinely incompatible on some
   APIs.** BepInEx's newer Harmony marks
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

7. *(Moot since 0.10.0: no UMM, and a single DLL.)* **A UMM GitHub issue
   (newman55/unity-mod-manager#133) specifically discusses Railroader mods
   with more than one DLL** (this mod ships
   `WebSocketSharp.dll` alongside the main assembly) **failing to load
   depending on UMM's install method.** Confirmed working on **DoorstopProxy**
   for this project. If a user reports the mod not loading at all under UMM,
   ask which install method they're on first.

8. **This project has two genuinely different "car id" schemes, and mixing
   them up fails silently.** `GetStableCarId(car)` (`car.Ident.ToString()`,
   falling back to `car.id`) is what this project uses for its own DTOs/
   client-side car identification -- but the *game's own* internal systems
   (`TrainController._carLookup`, and therefore anything that resolves a
   locomotive by id via `TrainController.Shared.TryGetCarForId`, which
   includes WaypointQueue's `LocoWaypointState.LocomotiveId`) are keyed by
   `car.id` alone. Passing `GetStableCarId(car)` into
   `WaypointQueueBridge.GetQueuedWaypoints` instead of `car.id` shipped once
   already: the base game's own single waypoint worked fine (it doesn't go
   through this lookup at all), but every Waypoint Queue lookup silently
   returned empty -- no exception, no log line, just an always-empty
   dictionary lookup -- exactly the kind of bug that looks like "the mod
   isn't installed" when it actually is. Any future code bridging to
   another mod's or the game's own id-keyed lookups should use `car.id`
   directly, not `GetStableCarId`.

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
   BepInEx entirely). Replaced in 0.10.0 by the game's built-in loader.
8. In-game `/minimap` console command (`/minimap_url` alias kept), properly
   registered into the game's own command system rather than raw-listened
   for, printing connection URLs.
9. Client-side "Text Size" slider (100-200%) scaling popups/legend (CSS
   variable) and canvas-drawn train labels (JS font-size multiplier),
   persisted via `localStorage`.
10. Track grade color-coding (white->yellow->red overlay, toggleable) and
    tap-bare-track-for-grade popup.
11. Engine Info panel: toggleable popout with a per-engine dropdown showing
    type/model/tractive effort/condition/fuel/status, whole-train aggregates
    (car count, consist weight, combined tractive effort), and an approximate
    max-tonnage calculation (`TE / (8 + grade% * 20)`) using the grade from
    the last-tapped point on the track.
12. Auto Engineer waypoint visualization, scoped to the engine currently
    selected in the Engine Info panel: the base game's own single
    `Orders.Waypoint`, plus (if the third-party Waypoint Queue mod is
    installed, detected via reflection -- see `WaypointQueueBridge.cs`) its
    full per-locomotive queue in order, each rendered as an oversized arrow
    icon with a tap-for-status popup (queue entries also show a numbered
    badge for their position in the queue). The popup shows both the mod's
    own live status text and a human-readable summary of each waypoint's
    actually-configured actions (couple/uncouple, refuel, wait, speed
    change, etc. -- see `BuildActionSummary` above), since status text alone
    is only ever meaningful for whichever waypoint is currently active. A
    persistent, dismissible notice in the message feed announces when the
    mod is detected.
13. Camera warp: right-click (long-press on touch) any point on the map for
    a small context menu with a "Warp camera here" option, which teleports
    whichever in-game camera is currently active (free/Strategy camera, or
    the first-person character) to that spot -- see `CameraSelector.
    JumpToPoint` above for the game API, and `HandleWarpCameraCommand` in
    `MinimapServerCore.cs` for the ground-height raycast this needed.
14. Railroader 2026.1 port (0.10.0): built-in mod loader, single-DLL
    packaging, and a Maintenance of Way overlay -- "Track Condition" toggle
    (condition color ramp, thick = speed restricted), unbuilt track always
    dashed, and condition/max speed/construction progress in the tap-track
    popup.
15. Curve labels (degrees of curve, one per curve at its sharpest segment,
    culled by on-screen length and overlap, own "Curve Label Size"
    slider), curvature/curve speed in the tap-track popup, and an
    "Options" popout holding every map display setting (track color
    modes, label toggles and sizes, Flip X/Z) so the header only has
    actions.

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
- **The 0.10.0 port compiles cleanly and the companion app's MoW overlay was
  checked in a headless browser against synthetic data, but none of it has
  been run in-game yet.** First things to verify in the real game: the mod
  shows up in the Mods screen and loads (Player.log), `/minimap` works,
  switch markers line up with the track (`TrackNode.Position`), supply-point
  and passenger-station icons appear, and real `track_condition` values look
  sane against the game's own track tooltip.
- **The WaypointQueue bridge is untested against a 2026.1 build of that
  mod.** It's reflection-only and degrades to "not installed" if its
  internals changed.

## Build

Single project, single DLL:

```powershell
Remove-Item -Recurse -Force obj, bin   # if anything seems stale
dotnet build RailroaderMinimap.csproj -c Release
```

Output: `bin\Release\net48\RailroaderMinimapServer.dll`. Install alongside
`Info.json` in a `RailroaderMinimapServer` folder under
`Documents\Railroader\Mods\`. The `DeployToModsFolder` target in the
`.csproj` does exactly that after every build (`ModDeployDir`; the release
workflow points it at a scratch folder instead).

The `.csproj` has hardcoded `HintPath`s for the game's own assemblies, all
under a `$(RailroaderDir)` MSBuild property near the top of the file --
update that one property (or pass `-p:RailroaderDir=...`) if the game is
installed somewhere else.
