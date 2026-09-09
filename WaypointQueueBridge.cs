using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Track; // Location

namespace RailroaderMinimapServer
{
    // Soft dependency on the third-party "Waypoint Queue" mod. WaypointQueue
    // is entirely optional -- everything here goes through reflection so
    // this project has no compile-time dependency on WaypointQueue.dll and
    // keeps working (just without queued-waypoint icons) when that mod
    // isn't installed. Location is the one exception: ManagedWaypoint.Location
    // is typed as Track.Location, a game type we already reference directly,
    // so a boxed value of it can be unboxed with a plain cast -- no
    // reflection needed for that specific value once we have it.
    internal static class WaypointQueueBridge
    {
        private static bool _initialized;
        private static bool _available;

        private static PropertyInfo _sharedProp; // static ModStateManager.Shared
        private static PropertyInfo _locoWaypointStatesProp; // instance IReadOnlyDictionary<string, LocoWaypointState>
        private static PropertyInfo _stateWaypointsProp; // LocoWaypointState.Waypoints -> List<ManagedWaypoint>
        private static PropertyInfo _waypointNameProp; // ManagedWaypoint.Name
        private static PropertyInfo _waypointStatusLabelProp; // ManagedWaypoint.StatusLabel
        private static PropertyInfo _waypointLocationProp; // ManagedWaypoint.Location (Track.Location)

        // Every OTHER public instance property on ManagedWaypoint, by name --
        // used by BuildActionSummary below to read whichever specific
        // configured-instruction fields it needs (CouplingSearchMode,
        // WillRefuel, WaitForDurationMinutes, etc: ~20 fields total). A
        // lookup dictionary instead of ~20 individual PropertyInfo fields,
        // since StatusLabel alone (e.g. "Inactive" for any not-yet-active
        // queued waypoint) turned out not to convey what a waypoint is
        // actually configured to DO -- only what it's doing RIGHT NOW.
        private static Dictionary<string, PropertyInfo> _waypointProps;

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                bool loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .Any(a => string.Equals(a.GetName().Name, "WaypointQueue", StringComparison.OrdinalIgnoreCase));
                if (!loaded) return;

                // ModStateManager is internal to WaypointQueue.dll, so it
                // can't be referenced by type name -- Type.GetType with an
                // assembly-qualified name works regardless of accessibility.
                Type modStateManagerType = Type.GetType("WaypointQueue.State.ModStateManager, WaypointQueue");
                Type locoWaypointStateType = Type.GetType("WaypointQueue.LocoWaypointState, WaypointQueue");
                Type managedWaypointType = Type.GetType("WaypointQueue.ManagedWaypoint, WaypointQueue");
                if (modStateManagerType == null || locoWaypointStateType == null || managedWaypointType == null)
                {
                    Log.Warning("WaypointQueue detected but its expected types weren't found -- integration disabled (mod version mismatch?)");
                    return;
                }

                _sharedProp = modStateManagerType.GetProperty("Shared", BindingFlags.Public | BindingFlags.Static);
                _locoWaypointStatesProp = modStateManagerType.GetProperty("LocoWaypointStates", BindingFlags.Public | BindingFlags.Instance);
                _stateWaypointsProp = locoWaypointStateType.GetProperty("Waypoints", BindingFlags.Public | BindingFlags.Instance);
                _waypointNameProp = managedWaypointType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                _waypointStatusLabelProp = managedWaypointType.GetProperty("StatusLabel", BindingFlags.Public | BindingFlags.Instance);
                _waypointLocationProp = managedWaypointType.GetProperty("Location", BindingFlags.Public | BindingFlags.Instance);

                _waypointProps = managedWaypointType
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .GroupBy(p => p.Name)
                    .ToDictionary(g => g.Key, g => g.First());

                _available = _sharedProp != null && _locoWaypointStatesProp != null && _stateWaypointsProp != null
                    && _waypointNameProp != null && _waypointStatusLabelProp != null && _waypointLocationProp != null;

                if (!_available)
                {
                    Log.Warning("WaypointQueue detected but its expected members weren't found -- integration disabled (mod version mismatch?)");
                }
                else
                {
                    Log.Info("WaypointQueue mod detected -- queued waypoints will be shown in the Engine Info panel.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"WaypointQueue integration disabled (reflection setup failed): {ex.Message}");
                _available = false;
            }
        }

        // True once the mod's expected types/members have all been found via
        // reflection (i.e. queued waypoints can actually be read), not just
        // that an assembly named "WaypointQueue" is loaded. Safe to poll
        // every tick -- EnsureInitialized only does real work once.
        public static bool IsAvailable()
        {
            EnsureInitialized();
            return _available;
        }

        // Returns each queued waypoint (in queue order) for the given
        // locomotive's stable car id, as (name, statusLabel, actions,
        // location). Empty (not null) if WaypointQueue isn't installed,
        // this locomotive has nothing queued, or its id isn't tracked yet.
        public static List<(string name, string statusLabel, List<string> actions, Location location)> GetQueuedWaypoints(string locomotiveId)
        {
            var result = new List<(string, string, List<string>, Location)>();
            EnsureInitialized();
            if (!_available || string.IsNullOrEmpty(locomotiveId)) return result;

            try
            {
                object shared = _sharedProp.GetValue(null);
                if (shared == null) return result;

                // The IReadOnlyDictionary<string, LocoWaypointState> interface
                // itself isn't castable to plain IDictionary, but the actual
                // backing object (a Dictionary<,>) is -- confirmed via
                // decompiling ModStateManager, which backs this property with
                // a plain Dictionary field.
                if (!(_locoWaypointStatesProp.GetValue(shared) is IDictionary statesDict)) return result;
                if (!statesDict.Contains(locomotiveId)) return result;

                object state = statesDict[locomotiveId];
                if (state == null) return result;

                if (!(_stateWaypointsProp.GetValue(state) is IEnumerable waypoints)) return result;

                foreach (object wp in waypoints)
                {
                    if (wp == null) continue;
                    string name = _waypointNameProp.GetValue(wp) as string;
                    string statusLabel = _waypointStatusLabelProp.GetValue(wp) as string;
                    if (!(_waypointLocationProp.GetValue(wp) is Location location)) continue;
                    result.Add((name, statusLabel, BuildActionSummary(wp), location));
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not read WaypointQueue state for '{locomotiveId}': {ex.Message}");
            }

            return result;
        }

        private static T GetProp<T>(object wp, string name, T fallback = default)
        {
            if (_waypointProps != null && _waypointProps.TryGetValue(name, out var pi))
            {
                try
                {
                    if (pi.GetValue(wp) is T typed) return typed;
                }
                catch
                {
                    // fall through to fallback
                }
            }
            return fallback;
        }

        // Enum-typed properties (CouplingSearchMode, UncouplingMode, etc.)
        // are declared by nested types private to WaypointQueue.dll, so
        // there's no compile-time enum type to cast a reflected value to --
        // reading the boxed value's own ToString() (the member name, e.g.
        // "Nearest"/"ByCount") avoids needing one.
        private static string GetPropEnumName(object wp, string name)
        {
            if (_waypointProps != null && _waypointProps.TryGetValue(name, out var pi))
            {
                try
                {
                    return pi.GetValue(wp)?.ToString();
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        // Translates a waypoint's raw configuration (coupling/uncoupling
        // mode, refuel/wait/speed-change settings, etc.) into short,
        // human-readable action lines -- what THIS waypoint is actually set
        // up to do, as distinct from ManagedWaypoint.StatusLabel (which only
        // ever reflects the mod's LIVE execution state, defaulting to
        // "Inactive" for every waypoint that isn't the currently-active one,
        // i.e. almost always misleadingly uninformative for queued entries).
        private static List<string> BuildActionSummary(object wp)
        {
            var actions = new List<string>();

            string couplingMode = GetPropEnumName(wp, "CouplingSearchMode");
            if (couplingMode == "Nearest")
            {
                actions.Add("Couple to nearest car");
            }
            else if (couplingMode == "SpecificCar")
            {
                string searchText = GetProp<string>(wp, "CouplingSearchText");
                actions.Add(!string.IsNullOrEmpty(searchText) ? $"Couple to car '{searchText}'" : "Couple to specific car");
            }

            string uncouplingMode = GetPropEnumName(wp, "UncouplingMode");
            switch (uncouplingMode)
            {
                case "ByCount":
                    int cutCount = GetProp(wp, "NumberOfCarsToCut", 0);
                    actions.Add($"Uncouple {cutCount} car" + (cutCount == 1 ? "" : "s"));
                    break;
                case "ByDestinationArea":
                case "ByDestinationIndustry":
                case "ByDestinationTrack":
                    actions.Add("Uncouple cars matching destination");
                    break;
                case "BySpecificCar":
                    actions.Add("Uncouple specific car");
                    break;
                case "AllExceptLocomotives":
                    actions.Add("Uncouple all cars (keep locomotives)");
                    break;
            }

            if (GetProp(wp, "WillRefuel", false))
            {
                string loadName = GetProp<string>(wp, "RefuelLoadName");
                actions.Add(!string.IsNullOrEmpty(loadName) ? $"Refuel {loadName}" : "Refuel");
            }

            if (GetProp(wp, "WillWait", false))
            {
                if (GetPropEnumName(wp, "DurationOrSpecificTime") == "SpecificTime")
                {
                    string timeStr = GetProp<string>(wp, "WaitUntilTimeString");
                    bool tomorrow = GetPropEnumName(wp, "WaitUntilDay") == "Tomorrow";
                    actions.Add($"Wait until {timeStr}" + (tomorrow ? " (tomorrow)" : ""));
                }
                else
                {
                    int minutes = GetProp(wp, "WaitForDurationMinutes", 0);
                    actions.Add($"Wait {minutes} minute" + (minutes == 1 ? "" : "s"));
                }
            }

            if (GetProp(wp, "WillChangeMaxSpeed", false))
            {
                int newMax = GetProp(wp, "MaxSpeedForChange", 0);
                actions.Add($"Change max speed to {newMax} mph");
            }

            // StopAtWaypoint defaults true (a plain stop) -- only worth a
            // line when the waypoint is instead a pass-through.
            if (!GetProp(wp, "StopAtWaypoint", true))
            {
                if (GetProp(wp, "WillLimitPassingSpeed", true))
                {
                    int targetSpeed = GetProp(wp, "WaypointTargetSpeed", 0);
                    actions.Add($"Pass through at {targetSpeed} mph (no stop)");
                }
                else
                {
                    actions.Add("Pass through without stopping");
                }
            }

            string timetableSymbol = GetProp<string>(wp, "TimetableSymbol");
            if (!string.IsNullOrEmpty(timetableSymbol))
            {
                actions.Add($"Timetable: {timetableSymbol}");
            }

            if (actions.Count == 0)
            {
                actions.Add("Move to this waypoint");
            }

            return actions;
        }
    }
}
