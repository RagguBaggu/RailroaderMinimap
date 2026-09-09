using System;
using System.Collections.Generic;

namespace RailroaderMinimapServer.Data
{
    // Every top-level message sent over the WebSocket carries a "type" field so
    // the client can dispatch on the raw JSON without guessing the shape from
    // the payload alone.
    public static class MessageType
    {
        public const string TrackNetwork = "track_network";
        public const string LiveState = "live_state";
        public const string SwitchState = "switch_state";
        public const string SignalAspectUpdate = "signal_aspect";
    }

    public class TrackNetworkDto
    {
        public string type { get; set; } = MessageType.TrackNetwork;
        public List<SegmentDto> segments { get; set; } = new List<SegmentDto>();
        public List<SwitchDto> switches { get; set; } = new List<SwitchDto>();
        public List<SignalDto> signals { get; set; } = new List<SignalDto>();
        public List<AreaDto> areas { get; set; } = new List<AreaDto>();
        public List<ServicePointDto> servicePoints { get; set; } = new List<ServicePointDto>();
        public List<PassengerStopDto> passengerStops { get; set; } = new List<PassengerStopDto>();
    }

    public class SegmentDto
    {
        public string id { get; set; }
        public string nodeA { get; set; }
        public string nodeB { get; set; }
        public float length { get; set; }

        // Effective speed limit in MPH (already resolved via TrackSegment.GetExpectedSpeedLimit(),
        // so this is never the raw "0 = use class default" sentinel value).
        public int speedLimit { get; set; }

        // Each entry is [x, z, gradePercent] in world coordinates -- gradePercent
        // is the track's grade (%) at that specific point (matching
        // Graph.GradeAtLocation's own formula), used for the client's
        // optional grade-color overlay. Positive/negative sign reflects the
        // pitch direction sampled from TrackSegment.End.A; the client only
        // ever uses the magnitude, so this isn't normalized further here.
        public List<float[]> points { get; set; } = new List<float[]>();
    }

    // Static geometry for a switch (junction). Sent once as part of the
    // track network cache. isThrown here is just the state *at cache time* --
    // live changes are pushed separately via SwitchStateUpdateDto so we don't
    // have to re-send the whole track network when a switch is thrown.
    public class SwitchDto
    {
        public string id { get; set; }
        public float[] position { get; set; }
        public bool isThrown { get; set; }

        // True for CTC-controlled switches (signal-equipped). Players don't
        // start with signals -- this is false for every switch until that
        // capability is unlocked, at which point some switches become
        // CTC-controlled and should render distinctly from plain switches.
        public bool isCTC { get; set; }
    }

    // Lightweight message broadcast only when a switch actually changes state
    // (via TrackNode.OnDidChangeThrown), instead of polling every switch on
    // every broadcast tick.
    public class SwitchStateUpdateDto
    {
        public string type { get; set; } = MessageType.SwitchState;
        public string id { get; set; }
        public bool isThrown { get; set; }
    }

    // A physical CTC signal -- a genuinely separate object from a switch,
    // with its own position and one of six real signal aspects (not just
    // on/off). Static geometry sent once as part of the track network cache;
    // live aspect changes are pushed separately via SignalAspectUpdateDto.
    public class SignalDto
    {
        public string id { get; set; }
        public float[] position { get; set; }

        // Serialized as a string (e.g. "Clear", "Stop") rather than an int,
        // matching Track.Signals.SignalAspect's six values, so the client
        // doesn't need to know the enum's underlying numeric order.
        public string aspect { get; set; }
    }

    // Lightweight message broadcast only when a signal's aspect actually
    // changes, instead of re-sending every signal on every tick.
    public class SignalAspectUpdateDto
    {
        public string type { get; set; } = MessageType.SignalAspectUpdate;
        public string id { get; set; }
        public string aspect { get; set; }
    }

    // A named point on the map -- yard, industry, interchange, etc. -- the
    // same labels the base game's own minimap shows (sourced from
    // UI.Map.MapLabel; see TrackExtractor for why). Static geometry sent
    // once as part of the track network cache; areas don't currently push
    // live updates since a label's position doesn't change during a
    // session, and only ever appear once actually unlocked.
    public class AreaDto
    {
        public string id { get; set; }
        public string name { get; set; }

        // Each entry is [x, z] in world coordinates, same convention as
        // SwitchDto/SignalDto positions.
        public float[] position { get; set; }
    }

    // A locomotive supply point -- water, coal, or diesel -- found via the
    // RollingStock.CarLoadTargetLoader supplying it (see TrackExtractor for
    // why: no base game minimap icon exists for these, unlike most other
    // markers here).
    public class ServicePointDto
    {
        public string id { get; set; }

        // "Water", "Coal", or "Diesel".
        public string kind { get; set; }
        public float[] position { get; set; }
    }

    // One passenger platform track. A single station can have more than one
    // (e.g. two parallel platform tracks), so this is one entry per
    // PassengerStop.TrackSpans element, not one per station -- several
    // entries can share the same `name`. The base game's own minimap shows
    // these via the actual 3D platform geometry (it's a literal camera
    // render), which we don't have, so unlike most other markers here this
    // one has no equivalent game icon/label to source from at all (see
    // TrackExtractor).
    public class PassengerStopDto
    {
        public string id { get; set; }
        public string name { get; set; }

        // The platform track's two endpoints -- NOT a single center point
        // plus a server-computed rotation. Sending both endpoints lets the
        // client derive rotation/length itself from their already-
        // toScreen()-transformed positions, which stays correct regardless
        // of the Flip X/Z toggles or current zoom, instead of us baking in
        // a rotation angle server-side that a flip toggle would invert.
        public float[] positionA { get; set; }
        public float[] positionB { get; set; }

        // The track span's real curve length (game/track units) -- distinct
        // from the straight-line distance between positionA/positionB,
        // which would underestimate a curved platform's true length.
        public float length { get; set; }
    }

    public class CarDataDto
    {
        public string id { get; set; }
        public float x { get; set; }
        public float z { get; set; }
        public float rotationY { get; set; }

        // False when the car's visual model (BodyTransform) isn't currently
        // loaded -- e.g. it's far from the player and the game has streamed
        // its mesh out, or it's mid chunk-load. Position is still a live,
        // accurate read in this case (resolved via the track graph instead
        // of the model transform) except in the rare case neither is
        // available, where it falls back to the last known position. Clients
        // can use this flag to style unloaded cars differently (e.g. a
        // slightly muted icon) without worrying the position itself is stale.
        public bool loaded { get; set; } = true;

        // True for locomotives (any type), false for regular rolling stock.
        // Lets the client render a visually distinct icon for engines.
        public bool isLocomotive { get; set; }

        // Human-readable car type, e.g. "2-8-2 Mikado" or "40ft Boxcar" --
        // shown in the client's tap-for-details popup.
        public string carType { get; set; }

        // Real axle-to-axle distance in game/track units, used by the
        // client to size each car's icon proportionally to its actual
        // length instead of a fixed guess. Roughly, not exactly, the car's
        // full length -- trucks/axles are typically inset from the car's
        // actual ends, so this slightly underestimates true length; the
        // client applies a fudge factor to compensate. Null/0 for a car
        // where this couldn't be computed, in which case the client should
        // fall back to its own fixed-size default.
        public float? lengthUnits { get; set; }

        // For a locomotive with a coupled tender: the tender's own cargo
        // (fuel/water), surfaced here so the engine's popup can show it
        // directly without a separate tap on the tender. Null for
        // non-locomotives, or a locomotive with no coupled tender.
        public List<string> tenderLoads { get; set; }

        // Cargo currently in each non-empty load slot, already formatted by
        // the game itself (e.g. "50,000 lbs Coal") via Load.QuantityString.
        // Empty for an empty car; multiple entries for multi-slot cars
        // (e.g. a multi-compartment tank car).
        public List<string> loads { get; set; }

        // True for Coach/Baggage cars. The base game excludes passenger cars
        // from destination-based coloring entirely (see IsFreight check in
        // TraincarColorUpdater), so clients should give these their own
        // distinct treatment rather than a destination color.
        public bool isPassenger { get; set; }

        // True for tender cars -- like engines and passenger cars, the
        // in-game minimap shows these as a plain neutral color rather than
        // destination-colored.
        public bool isTender { get; set; }

        // [r, g, b] (0-255) matching Area.tagColor for this car's waybill
        // destination -- the same color the game's own minimap uses. Null if
        // the car has no active waybill or its destination didn't resolve to
        // an Area (e.g. an override/repair destination we don't chase down).
        public int[] destinationColor { get; set; }

        // Human-readable destination name (e.g. an industry or yard name),
        // shown in the tap-for-details popup. Null under the same conditions
        // as destinationColor.
        public string destinationName { get; set; }

        // True once this car has actually reached destinationName (matches
        // the game's own OpsControllerExtensions.TryGetDestinationInfo).
        // Null under the same conditions as destinationColor/destinationName
        // (no active waybill). The client uses the false->true edge to flash
        // a "just arrived" indicator, and any change while destinationName
        // itself changes to flash a "new destination assigned" indicator --
        // both transient, not sent as their own field, since detecting the
        // edge only requires comparing this tick's value to the last one
        // already held client-side.
        public bool? atDestination { get; set; }

        // True while this car is running hot (Car.HasHotbox) -- a real
        // mechanical problem the player needs to address (oil it or stop),
        // not just informational, so the client renders this prominently
        // and keeps it visible for as long as it stays true.
        public bool hasHotbox { get; set; }

        // True while this car's handbrake is set (Car.air.handbrakeApplied).
        // Same "stays visible until resolved" treatment as hasHotbox.
        public bool handbrakeApplied { get; set; }

        // The nearest Area to this car's CURRENT position (distinct from
        // destinationName, which is where it's headed) -- only resolved
        // when hasHotbox is true, since it's the one place this is actually
        // used (the hotbox message feed's "which region" text). Null
        // otherwise.
        public string nearestAreaName { get; set; }

        // Car.Weight (empty + current load), in pounds. Sent for every car
        // (cheap property read) since it feeds the engine panel's gross
        // train weight even for non-locomotive cars, not just the
        // locomotive itself.
        public float weight { get; set; }

        // Car.Condition, 0-1 (repair state). Sent for every car.
        public float condition { get; set; }

        // "Steam" or "Diesel" -- null for non-locomotives.
        public string locomotiveType { get; set; }

        // BaseLocomotive.RatedTractiveEffort (lbs) -- this engine's own
        // rated/starting tractive effort, distinct from the live,
        // throttle-dependent Car.TractiveEffort. Null for non-locomotives.
        public float? ratedTractiveEffort { get; set; }

        // The following three are about the WHOLE coupled train this
        // locomotive is part of (via Car.EnumerateCoupled(), which walks
        // the full physically-connected consist regardless of which end
        // this car sits at), not just this one car. Null for
        // non-locomotives, since the engine panel is the only current
        // consumer and only ever looks these up for a selected engine.
        // trainCarCount excludes locomotives and tenders (revenue/passenger
        // cars only, matching how a railroader would say "a 40-car train").
        public int? trainCarCount { get; set; }

        // Total weight (tons) of every car in the coupled train, including
        // locomotives and tenders.
        public float? trainGrossWeightTons { get; set; }

        // Sum of RatedTractiveEffort across every locomotive in the coupled
        // train (this one included).
        public float? trainCombinedTractiveEffort { get; set; }

        // This locomotive's own single Auto Engineer waypoint order (Model.AI
        // Orders.Waypoint, when Mode == AutoEngineerMode.Waypoint) -- the
        // base game's native, one-at-a-time waypoint system. Null for
        // non-locomotives, and for a locomotive not currently in Waypoint
        // mode.
        public WaypointDto autoEngineerWaypoint { get; set; }

        // The full queued waypoint list from the third-party "Waypoint
        // Queue" mod, in queue order -- always an empty list (never null)
        // for a locomotive, whether that's because nothing is queued or
        // because the mod isn't installed at all (see WaypointQueueBridge,
        // which returns an empty list either way). Null only for
        // non-locomotives.
        public List<WaypointDto> queuedWaypoints { get; set; }
    }

    // A single Auto Engineer destination -- either the base game's own
    // single waypoint order, or one entry from the Waypoint Queue mod's
    // per-locomotive queue. Rendered client-side as a larger arrow icon
    // distinct from the fixed map markers (switches/signals/etc.) above.
    public class WaypointDto
    {
        // Short label identifying the waypoint (Waypoint Queue's own
        // ManagedWaypoint.Name, e.g. "Waypoint 1"; a fixed "Waypoint" for
        // the base game's single order, which has no name of its own).
        public string label { get; set; }

        // What this waypoint is currently doing, in the mod's own words
        // (ManagedWaypoint.StatusLabel, e.g. "Running to waypoint",
        // "Refueling Coal") -- LIVE execution state, not configuration, so
        // it reads "Inactive" for every queued waypoint that isn't the one
        // currently being run (see `actions` below for what it's actually
        // configured to do). For the base game's single order this is just
        // the Orders.Mode name ("Waypoint"), since the base game has no
        // richer status text to surface.
        public string statusLabel { get; set; }

        // Human-readable configured instructions for this waypoint (e.g.
        // "Couple to nearest car", "Refuel Coal", "Wait 5 minutes"),
        // translated server-side from Waypoint Queue's raw settings (see
        // WaypointQueueBridge.BuildActionSummary) -- unlike statusLabel,
        // this reflects what the waypoint will DO whenever it runs,
        // regardless of whether it's the active one right now. Null for the
        // base game's single order, which has no configurable instructions
        // of its own (it's just "go here"); never null/empty for a
        // Waypoint Queue entry (falls back to "Move to this waypoint").
        public List<string> actions { get; set; }

        // [x, z] in world coordinates, same convention as every other
        // position field in this file.
        public float[] position { get; set; }
    }

    public class LiveStatePayloadDto
    {
        public string type { get; set; } = MessageType.LiveState;
        public float timestamp { get; set; }
        public List<CarDataDto> rollingStock { get; set; } = new List<CarDataDto>();

        // True once the third-party "Waypoint Queue" mod has been detected
        // server-side (see WaypointQueueBridge) -- lets the client show a
        // one-time "enhanced waypoint tracking available" notice instead of
        // silently having no way to distinguish "mod not installed" from
        // "nothing queued right now" for any given engine.
        public bool waypointQueueAvailable { get; set; }
    }
}
