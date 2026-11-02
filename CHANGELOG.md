# Changelog

All notable changes to this project are documented here. Format loosely
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

Version 0.10.0 (already set in `Info.json` and `PluginVersion`). Requires Railroader 2026.1 (the Maintenance of Way update) or newer; players on older game versions should stay on 0.9.0.

### Added

- Maintenance of Way track condition overlay ("Track Condition" toggle) — colors every segment by its condition (the same percentage the game's own track tooltip shows), and draws track under a wear speed restriction thicker
- Track that hasn't been built yet is drawn dashed, in every mode, instead of looking like usable track
- Tapping bare track now also shows its class, condition, and current max speed (and why), or for track under construction, its progress and which materials it still needs
- New `track_condition` WebSocket message: a full per-segment snapshot after every `track_network`, then only segments that changed (polled every 5 seconds)

### Changed

- Engine Info panel: "Gross weight" is now **Consist Weight**, and it's shown even for a light engine with no cars attached. The engine, its tender, and their fuel/water/coal are always counted, and it's rounded up to whole tons the same way the game's own HUD rounds its tonnage, so the two match
- Loads through Railroader's built-in mod loader instead of Unity Mod Manager. Install into `Documents\Railroader\Mods\RailroaderMinimapServer`, turn on Preferences > Experimental > "Allow Unsafe DLL Mods", and enable the mod in the game's Mods screen. It's a client-scoped mod: not recorded in saves, not required of other multiplayer players
- The mod is now a single DLL plus `Info.json`: it uses the WebSocketSharp library that now ships with the game instead of bundling its own copy, and no longer uses Harmony (which only exists in a game install when another mod loader put it there)
- Rebuilt against Railroader 2026.1's reorganized game code (`Railroader.dll` replaces `Assembly-CSharp.dll`/`Definition.dll`; `CarType` replaces `CarArchetype`; track nodes, segments, passenger stops, and loads are no longer Unity objects; console commands use the new attribute-based system; game events moved to `EventBus`)
- Switches, signals, and Maintenance of Way state are now dropped and re-read when a different save is loaded, instead of potentially holding on to the previous save's objects

### Removed

- Unity Mod Manager support
- The `/minimap_url` console alias (it made `/mini` ambiguous under the game's new prefix matching); `/minimap` still works

## [0.9.0] - 2026-09-09

### Added

- Track grade color-coding — an optional white → yellow → red overlay on every track segment, plus tap-any-bare-track-for-its-grade
- Engine Info panel — a toggleable per-engine dropdown showing type, model, tractive effort, condition, fuel, and status; whole-train aggregates (car count, gross weight, combined tractive effort); and an approximate max-tonnage calculation for the last-tapped track grade
- Auto Engineer waypoint visualization, scoped to the engine selected in the Engine Info panel — the base game's own single waypoint order, plus (if the third-party **Waypoint Queue** mod is installed) its complete queue in order, each shown as a numbered arrow icon with a tap-for-details popup covering both live status and a plain-language summary of what it's actually configured to do
- Camera warp — right-click (or long-press on touch) anywhere on the map to teleport the in-game free camera or first-person character there

### Fixed

- The `/minimap` console command is now a properly registered game command (`UI.Console.IConsoleCommand`) instead of a raw input listener. Previously, typing it *without* a leading `/` also broadcast it as in-game chat, and typing it *with* a `/` still printed the game's own "Command not recognized." alongside the real output, since a raw listener has no way to suppress the game's own handling of the same input.

### Changed

- Copyright/author attribution in `LICENSE` and `Info.json` now credits "RagguBaggu" instead of a real name.

## [0.8.0] - 2026-09-08

### Added

- Yard, industry, and interchange area name labels, sourced from the same `UI.Map.MapLabel` components the base game's own minimap uses (so modded content is picked up automatically too)
- Car status indicators — hotbox and handbrake badges that persist until resolved, plus transient "spotted"/"new destination" flashes — backed by a dismissible message feed
- Locomotive supply points (water/coal/diesel) and passenger station platforms, each with their own distinct icon

### Changed

- Ported from a dual BepInEx/Unity Mod Manager setup to Unity Mod Manager only

[Unreleased]: https://github.com/RagguBaggu/RailroaderMinimap/compare/v0.9.0...HEAD
[0.9.0]: https://github.com/RagguBaggu/RailroaderMinimap/releases/tag/v0.9.0
