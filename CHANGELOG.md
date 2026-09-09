# Changelog

All notable changes to this project are documented here. Format loosely
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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
