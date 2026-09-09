using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GalaSoft.MvvmLight.Messaging;
using Game.Events; // GraphDidRebuildCollections
using Game.Messages; // RequestSetSwitch
using Game.State; // StateManager
using HarmonyLib;
using UnityEngine;
using Newtonsoft.Json;
using Model; // Railroader's vehicle namespace (Car, Locomotive)
using Model.AI; // AutoEngineerPersistence, Orders, AutoEngineerMode
using Model.Definition; // CarArchetype
using Model.Ops; // OpsController, Area, OpsCarPosition, Waybill
using Model.Ops.Definition; // Load
using Track; // TrackNode, Graph, Location
using Track.Signals; // CTCSignal, SignalStorage, SignalAspect
using Helpers; // WorldTransformer.WorldToGame
using RailroaderMinimapServer.Data;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace RailroaderMinimapServer
{
    // Core logic, kept separate from the loader-specific entry point
    // (UnityModManagerEntry.cs handles logging setup and instantiation) so
    // the two concerns don't tangle together.
    public class MinimapServerCore : MonoBehaviour
    {
        public const string PluginGuid = "com.community.railroader.minimap";
        public const string PluginName = "Railroader Minimap Server";
        public const string PluginVersion = "0.9.0";

        private const int HttpPort = 8080;
        private const int WsPort = 8081;
        private const string ServicePath = "/ws";

        // WebSocketSharp implements the WebSocket protocol itself over raw
        // sockets -- Mono's System.Net.HttpListener does not implement
        // WebSocket support at all (AcceptWebSocketAsync throws
        // NotImplementedException at runtime under Unity's Mono, regardless
        // of platform), so we can't use HttpListener here.
        // Two separate listeners, deliberately: WebSocketSharp's HttpServer
        // (which combines both jobs) has a known, unresolved bug where
        // static-content responses come back empty in the browser
        // (sta/websocket-sharp#551) -- its WebSocket handling is a completely
        // separate, working code path, so we keep using WebSocketServer for
        // that, and fall back to plain System.Net.HttpListener (reliable
        // under Mono for ordinary HTTP GET/response -- it was only the
        // WebSocket-upgrade extension methods that were broken) for serving
        // the static companion app page.
        private HttpListener _httpListener;
        private WebSocketServer _wsServer;
        private CancellationTokenSource _httpCts;

        private Harmony _harmony;

        private readonly ConcurrentQueue<string> _inboundQueue = new ConcurrentQueue<string>();

        private string _cachedTrackJson = null;

        // A single mode change or bulk CTC operation can trigger our
        // rebuild-worthy events (GraphDidRebuildCollections, system mode
        // change) dozens or hundreds of times in rapid succession -- e.g.
        // once per affected switch. Without throttling, each one
        // synchronously re-extracts the entire network, serializes it, and
        // broadcasts it, compounding into a multi-second hang. Instead, mark
        // the cache dirty immediately but only actually do that expensive
        // work at most once per interval, in Update().
        private bool _trackCacheNeedsRebroadcast = false;
        private float _lastTrackCacheRebroadcastTime = -999f;
        private const float TrackCacheRebroadcastMinInterval = 1.0f;
        private float _broadcastTimer = 0f;
        private const float BroadcastInterval = 0.1f; // 10 Hz

        private float _carDebugLogTimer = 0f;
        private const float CarDebugLogInterval = 5.0f;

        // Last known live position per car, keyed by a stable car id. Used
        // only as a final fallback if a car has neither a captured
        // UpdateMapIconPosition sample yet nor a valid track Location (e.g.
        // in the first instant after it's created).
        private readonly Dictionary<string, CarDataDto> _lastKnownState = new Dictionary<string, CarDataDto>();

        // Handlers registered against TrackNode.OnDidChangeThrown, kept so we
        // can unsubscribe cleanly on shutdown/reload.
        private readonly Dictionary<TrackNode, Action> _switchHandlers = new Dictionary<TrackNode, Action>();

        // SignalStorage isn't a global singleton itself -- reached via
        // CTCPanelController.Shared.GetComponentInParent<SignalStorage>(),
        // same instance the whole CTC system uses internally.
        private SignalStorage _signalStorage;

        // Tracks whether TryHookConsole has already made its one attempt to
        // register the /minimap command, resolved lazily since the Console
        // UI may not exist yet the moment this plugin's Awake() runs.
        private bool _consoleHooked = false;
        private IDisposable _systemModeObserver;
        private readonly Dictionary<string, IDisposable> _signalObservers = new Dictionary<string, IDisposable>();

        private void Awake()
        {
            Log.Info($"{PluginName} initializing...");

            // DontDestroyOnLoad only takes effect on a root-level GameObject.
            if (transform.parent != null)
            {
                Log.Info($"Plugin GameObject was parented under '{transform.parent.name}' -- detaching so it can persist across scene loads.");
                transform.SetParent(null, worldPositionStays: false);
            }
            else
            {
                Log.Info("Plugin GameObject is already at scene root.");
            }

            UnityEngine.Object.DontDestroyOnLoad(gameObject);

            // Graph.RebuildCollections() broadcasts this whenever the track
            // network changes -- e.g. new track unlocked via milestones, or
            // existing track reorganized (water/coaling tower additions,
            // etc.). Subscribing means the cache invalidates and refreshes
            // itself automatically instead of silently going stale for the
            // rest of the session.
            Messenger.Default.Register<GraphDidRebuildCollections>(this, OnGraphRebuilt);

            // Patches Car.UpdateMapIconPosition to capture the same live
            // position/rotation the game's own minimap uses -- confirmed (by
            // examining the Map Enhancer mod) to be called for every car
            // regardless of whether its visual model is currently loaded,
            // which is far more reliable than reading BodyTransform directly.
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());

            StartServer();
        }

        private void Update()
        {
            try
            {
                UpdateInternal();
            }
            catch (Exception ex)
            {
                // An uncaught exception here can silently stop Unity from
                // calling Update() again for the rest of the session --
                // exactly what happened with the CTCPanelController.Shared
                // stale-reference bug. Whatever goes wrong in the future,
                // log it and keep going rather than going dark entirely.
                Log.Error($"Unhandled exception in Update(): {ex}");
            }
        }

        private void UpdateInternal()
        {
            if (!_consoleHooked) TryHookConsole();

            // 1. Process client requests on the main thread
            while (_inboundQueue.TryDequeue(out string command))
            {
                if (command == "GET_TRACK_DATA")
                {
                    // Force a genuine re-extraction rather than potentially
                    // re-serving a stale cache -- this is the client's
                    // explicit "refresh" action, so it should always reflect
                    // current game state (e.g. a switch's CTC status can
                    // apparently change via an ABS/CTC mode switch without
                    // that triggering Graph's own rebuild event, which is
                    // what normally invalidates this cache automatically).
                    _cachedTrackJson = null;
                    EnsureTrackCache();
                    if (!string.IsNullOrEmpty(_cachedTrackJson))
                    {
                        Broadcast(_cachedTrackJson);
                    }
                }
                else if (command.StartsWith("SET_SWITCH:"))
                {
                    HandleSetSwitchCommand(command.Substring("SET_SWITCH:".Length));
                }
                else if (command.StartsWith("WARP_CAMERA:"))
                {
                    HandleWarpCameraCommand(command.Substring("WARP_CAMERA:".Length));
                }
            }

            // Handle a track cache invalidated by OnGraphRebuilt/
            // OnSystemModeChanged -- throttled, since a bulk operation (e.g.
            // toggling CTC status on every switch in an interlocking at once)
            // can mark this dirty dozens of times in a single frame. Only
            // actually pay for the expensive rebuild+broadcast at most once
            // per interval, regardless of how many invalidations arrived.
            if (_trackCacheNeedsRebroadcast && Time.time - _lastTrackCacheRebroadcastTime >= TrackCacheRebroadcastMinInterval)
            {
                _trackCacheNeedsRebroadcast = false;
                _lastTrackCacheRebroadcastTime = Time.time;

                if (HasConnectedClients())
                {
                    EnsureTrackCache();
                    if (!string.IsNullOrEmpty(_cachedTrackJson))
                    {
                        Broadcast(_cachedTrackJson);
                    }
                }
            }

            bool hasClients = HasConnectedClients();

            // Diagnostic logging, throttled -- runs regardless of the
            // hasClients gate below.
            _carDebugLogTimer += Time.deltaTime;
            if (_carDebugLogTimer >= CarDebugLogInterval)
            {
                _carDebugLogTimer = 0f;
                LogCarDiagnostics(hasClients);
            }

            if (!hasClients) return;

            // 2. Broadcast vehicle telemetry. TrainController.Shared.Cars is
            // an already-maintained collection (not a scene scan), so there's
            // no need to cache/rescan it periodically the way FindObjectsOfType
            // would have required.
            _broadcastTimer += Time.deltaTime;
            if (_broadcastTimer >= BroadcastInterval)
            {
                _broadcastTimer = 0f;
                string payload = GatherLiveStateJson();
                if (!string.IsNullOrEmpty(payload))
                {
                    Broadcast(payload);
                }
            }
        }

        private void OnGraphRebuilt(GraphDidRebuildCollections message)
        {
            // Existing switch/signal subscriptions may reference objects that
            // no longer exist, or miss newly added ones -- drop them now;
            // EnsureTrackCache re-subscribes against the fresh set.
            UnsubscribeFromSwitchEvents();
            UnsubscribeFromSignalEvents();
            _cachedTrackJson = null;
            _trackCacheNeedsRebroadcast = true;
        }

        private void EnsureTrackCache()
        {
            if (_cachedTrackJson == null)
            {
                Log.Info("Building track geometry cache...");

                TrackNetworkExtractionResult result;
                try
                {
                    result = TrackExtractor.ExportActiveNetwork(sampleIntervalMeters: 6.0f);
                }
                catch (Exception ex)
                {
                    // TrackExtractor has its own per-item try/catch around
                    // anything read from live game state, but this is a
                    // last-resort backstop: without it, an exception here
                    // would leave _cachedTrackJson null forever and retry
                    // (and fail identically) every single tick, since
                    // nothing else ever sets it -- silently going dark for
                    // the rest of the session instead of just skipping this
                    // one attempt. Same reasoning as the top-level Update()
                    // catch elsewhere in this file.
                    Log.Error($"Track extraction threw, track data unavailable this attempt: {ex}");
                    return;
                }

                if (result.Network.segments.Count == 0)
                {
                    // Almost certainly means Graph.Shared wasn't finished
                    // building yet -- e.g. a client's auto-request raced
                    // ahead of a save that was still loading. Don't cache
                    // this permanently; retry fresh on the next request
                    // instead of getting stuck empty for the rest of the
                    // session.
                    Log.Warning("Track network came back empty (0 segments) -- Graph probably isn't ready yet. Not caching; will retry on the next request.");
                    return;
                }

                _cachedTrackJson = JsonConvert.SerializeObject(result.Network);
                SubscribeToSwitchEvents(result.SwitchNodes);
                SubscribeToSignalEvents(result.Signals);
                Log.Info($"Cached {result.Network.segments.Count} segments, {result.Network.switches.Count} switches, {result.Network.signals.Count} signals, {result.Network.areas.Count} areas, {result.Network.servicePoints.Count} service points, and {result.Network.passengerStops.Count} passenger stops.");
            }
        }

        private void LogCarDiagnostics(bool hasClients)
        {
            var controller = TrainController.Shared;
            Graph graph = Graph.Shared;

            if (controller == null)
            {
                Log.Info($"[CarDiag] hasClients={hasClients}, TrainController.Shared is null.");
                return;
            }

            int total = 0, viaGraph = 0, viaMapIconFallback = 0, lastKnownFallback = 0, skipped = 0;

            foreach (var car in controller.Cars)
            {
                if (car == null) continue;
                total++;

                if (graph != null && car.LocationF.IsValid && car.LocationR.IsValid)
                {
                    viaGraph++;
                }
                else if (Car_UpdateMapIconPosition_Patch.LatestPositions.ContainsKey(GetStableCarId(car)))
                {
                    viaMapIconFallback++;
                }
                else if (_lastKnownState.ContainsKey(GetStableCarId(car)))
                {
                    lastKnownFallback++;
                }
                else
                {
                    skipped++;
                }
            }

            Log.Info(
                $"[CarDiag] hasClients={hasClients}, GraphShared={(graph != null)}, " +
                $"totalCars={total}, viaGraph={viaGraph}, viaMapIconFallback={viaMapIconFallback}, " +
                $"lastKnownFallback={lastKnownFallback}, skippedEntirely={skipped}, " +
                $"harmonyDictSize={Car_UpdateMapIconPosition_Patch.LatestPositions.Count}");
        }

        private string GatherLiveStateJson()
        {
            var controller = TrainController.Shared;
            if (controller == null) return null;

            var payload = new LiveStatePayloadDto { timestamp = Time.time, waypointQueueAvailable = WaypointQueueBridge.IsAvailable() };
            Graph graph = Graph.Shared;

            foreach (var car in controller.Cars)
            {
                if (car == null) continue;

                string carId = GetStableCarId(car);
                CarDataDto dto;

                bool isLoco = false;
                string carType = null;
                bool isPassenger = false;
                bool isTender = false;
                try
                {
                    isLoco = car.IsLocomotive;
                    // Car.CarType is just a short category code (e.g. "LS"
                    // for any steam locomotive, "LD" for any diesel one) --
                    // NOT a descriptive class/model name, despite the
                    // property name suggesting otherwise. The real display
                    // name the game's own UI uses (confirmed by decompiling
                    // TagNameForCar, e.g. "P-48 Pacific" or "U30C") is
                    // Car.DefinitionInfo.Metadata.Name, with CarType only as
                    // its fallback when that's empty -- same resolution
                    // order used here.
                    carType = !string.IsNullOrEmpty(car.DefinitionInfo.Metadata.Name)
                        ? car.DefinitionInfo.Metadata.Name
                        : car.CarType;
                    // The base game excludes passenger cars from destination
                    // coloring entirely (see IsFreight check in the
                    // TraincarColorUpdater coroutine) -- Coach/Baggage are
                    // the passenger-carrying archetypes.
                    isPassenger = car.Archetype == CarArchetype.Coach || car.Archetype == CarArchetype.Baggage;
                    isTender = car.Archetype == CarArchetype.Tender;
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not read car type for '{carId}': {ex.Message}");
                }

                List<string> loads = ReadCarLoads(car, carId);

                // A steam locomotive's own load slots are typically empty --
                // fuel/water live on its tender instead, a separate Car. If
                // this is a locomotive, find its coupled tender (checking
                // both ends, since orientation varies) and surface the
                // tender's cargo here too, so the engine's own popup can
                // show fuel/water without a separate tap on the tender.
                List<string> tenderLoads = null;
                if (isLoco)
                {
                    try
                    {
                        Car tender = car.CoupledTo(Car.LogicalEnd.A);
                        if (tender == null || tender.Archetype != CarArchetype.Tender)
                        {
                            tender = car.CoupledTo(Car.LogicalEnd.B);
                        }
                        if (tender != null && tender.Archetype == CarArchetype.Tender)
                        {
                            tenderLoads = ReadCarLoads(tender, GetStableCarId(tender));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Could not find coupled tender for '{carId}': {ex.Message}");
                    }
                }

                (int[] destColor, string destName, bool? atDestination) = isPassenger ? (null, null, null) : GetDestinationInfo(car);

                bool hasHotbox = car.HasHotbox;
                bool handbrakeApplied = car.air != null && car.air.handbrakeApplied;

                // Only resolved when actually needed (a hotbox message needs
                // "which region"), since ClosestArea does a scene-wide
                // distance scan over every Area -- not worth paying for on
                // every car, every broadcast tick, when almost none are ever
                // hotboxed at a given moment.
                string nearestAreaName = hasHotbox ? GetNearestAreaName(car) : null;

                float weight = 0f;
                float condition = 0f;
                try
                {
                    weight = car.Weight;
                    condition = car.Condition;
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not read weight/condition for '{carId}': {ex.Message}");
                }

                // Only resolved for locomotives -- EnumerateCoupled walks the
                // whole train, not worth paying for on every freight/passenger
                // car when the engine panel is the only current consumer.
                (string locomotiveType, float? ratedTractiveEffort, int? trainCarCount, float? trainGrossWeightTons, float? trainCombinedTractiveEffort) =
                    isLoco ? GetLocomotiveTrainInfo(car) : (null, null, null, null, null);

                // Also locomotive-only -- resolving Auto Engineer waypoint
                // locations means a Graph lookup (or several, for a
                // WaypointQueue queue), same cost rationale as
                // GetLocomotiveTrainInfo above.
                (WaypointDto autoEngineerWaypoint, List<WaypointDto> queuedWaypoints) =
                    isLoco ? GetLocomotiveWaypoints(car, graph, carId) : (null, null);

                // PRIMARY: the car's true geometric center, recomputed fresh
                // from track topology on every call (never a cached/stale
                // value). This used to read graph.GetPositionRotation(
                // car.LocationF) directly -- but LocationF/LocationR are the
                // car's FRONT and REAR axle reference points, not its
                // center (confirmed directly in Car.cs: GetCenterPosition
                // is literally Vector3.Lerp(positionF, positionR, 0.5f)).
                // Rendering an icon centered on the front axle instead of
                // the true center meant every car's icon was offset by
                // roughly half its own length, which for closely-coupled
                // cars could place two different cars' rendered centers
                // much closer together than their real centers actually
                // are -- exactly the near-overlapping icons this fixes.
                Vector3? centerPos = null;
                Quaternion? centerRot = null;
                float? lengthUnits = null;
                if (graph != null && car.LocationF.IsValid && car.LocationR.IsValid)
                {
                    try
                    {
                        centerPos = car.GetCenterPosition(graph);
                        centerRot = car.GetCenterRotation(graph);
                        // GetPositionFR hits the same internal cache
                        // GetCenterPosition/GetCenterRotation just populated,
                        // so this is effectively free, not a third
                        // recomputation.
                        car.GetPositionFR(graph, out Vector3 posF, out Vector3 posR);
                        lengthUnits = Vector3.Distance(posF, posR);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Could not compute center position for '{carId}': {ex.Message}");
                    }
                }

                if (centerPos.HasValue && centerRot.HasValue)
                {
                    // The Harmony-captured MapIcon position below is only
                    // refreshed when the game calls Car.UpdateMapIconPosition,
                    // which happens as part of active movement -- a stationary
                    // car has no reason for that to fire, so if the world's
                    // coordinate origin rebases underneath it (confirmed this
                    // game has such a system, via CarMover.WorldDidMove/
                    // "MoveWorld" logs -- camera teleports and waking a car up
                    // from being unloaded are exactly the kind of event that
                    // can trigger this), the cached value goes stale until the
                    // car moves again. Location-based position has no such
                    // staleness window since it's never cached.
                    Vector3 pos = centerPos.Value;
                    dto = new CarDataDto
                    {
                        id = carId,
                        x = pos.x,
                        z = MapCoordinates.MapZ(pos.z),
                        rotationY = centerRot.Value.eulerAngles.y,
                        loaded = car.BodyTransform != null,
                        isLocomotive = isLoco,
                        carType = carType,
                        isPassenger = isPassenger,
                        isTender = isTender,
                        lengthUnits = lengthUnits,
                        loads = loads,
                        tenderLoads = tenderLoads,
                        destinationColor = destColor,
                        destinationName = destName,
                        atDestination = atDestination,
                        hasHotbox = hasHotbox,
                        handbrakeApplied = handbrakeApplied,
                        nearestAreaName = nearestAreaName,
                        weight = weight,
                        condition = condition,
                        locomotiveType = locomotiveType,
                        ratedTractiveEffort = ratedTractiveEffort,
                        trainCarCount = trainCarCount,
                        trainGrossWeightTons = trainGrossWeightTons,
                        trainCombinedTractiveEffort = trainCombinedTractiveEffort,
                        autoEngineerWaypoint = autoEngineerWaypoint,
                        queuedWaypoints = queuedWaypoints
                    };
                    _lastKnownState[carId] = dto;
                }
                else if (Car_UpdateMapIconPosition_Patch.LatestPositions.TryGetValue(carId, out var capturedPosRot))
                {
                    // Fallback for the rare case a car doesn't have a valid
                    // track Location yet (e.g. very early in setup) but has
                    // already reported a MapIcon position. Still needs the
                    // game-space conversion -- see WorldTransformer note above.
                    Vector3 gamePos = WorldTransformer.WorldToGame(capturedPosRot.position);
                    dto = new CarDataDto
                    {
                        id = carId,
                        x = gamePos.x,
                        z = MapCoordinates.MapZ(gamePos.z),
                        rotationY = capturedPosRot.rotation.eulerAngles.y,
                        loaded = car.BodyTransform != null,
                        isLocomotive = isLoco,
                        carType = carType,
                        isPassenger = isPassenger,
                        isTender = isTender,
                        loads = loads,
                        tenderLoads = tenderLoads,
                        destinationColor = destColor,
                        destinationName = destName,
                        atDestination = atDestination,
                        hasHotbox = hasHotbox,
                        handbrakeApplied = handbrakeApplied,
                        nearestAreaName = nearestAreaName,
                        weight = weight,
                        condition = condition,
                        locomotiveType = locomotiveType,
                        ratedTractiveEffort = ratedTractiveEffort,
                        trainCarCount = trainCarCount,
                        trainGrossWeightTons = trainGrossWeightTons,
                        trainCombinedTractiveEffort = trainCombinedTractiveEffort,
                        autoEngineerWaypoint = autoEngineerWaypoint,
                        queuedWaypoints = queuedWaypoints
                    };
                    _lastKnownState[carId] = dto;
                }
                else if (_lastKnownState.TryGetValue(carId, out var lastKnown))
                {
                    dto = lastKnown;
                }
                else
                {
                    continue;
                }

                payload.rollingStock.Add(dto);
            }

            return payload.rollingStock.Count > 0 ? JsonConvert.SerializeObject(payload) : null;
        }

        // Resolves a car's waybill destination to the same Area.tagColor the
        // game's own minimap uses (found by examining Map Enhancer's
        // TraincarColorUpdater coroutine, which reads this exact field).
        // Deliberately simpler than the base game's own TryGetDestinationInfo:
        // we skip the "override destination" (e.g. sent for repair) case,
        // since that's a rare edge case and reading car.Waybill directly is
        // sufficient for general destination coloring.
        // Reads every non-empty load slot on a car, formatted with the
        // game's own Load.QuantityString (e.g. "50,000 lbs Coal"). Shared
        // between a car's own cargo and a locomotive's coupled tender.
        private static List<string> ReadCarLoads(Car car, string carIdForLogging)
        {
            List<string> loads = null;
            try
            {
                int slotCount = car.Definition.LoadSlots.Count;
                if (slotCount > 0)
                {
                    loads = new List<string>();
                    for (int slot = 0; slot < slotCount; slot++)
                    {
                        CarLoadInfo? loadInfo = car.GetLoadInfo(slot);
                        if (!loadInfo.HasValue || string.IsNullOrEmpty(loadInfo.Value.LoadId)) continue;

                        Load load = CarPrototypeLibrary.instance?.LoadForId(loadInfo.Value.LoadId);
                        if (load == null) continue;

                        loads.Add(load.QuantityString(loadInfo.Value.Quantity));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not read load info for '{carIdForLogging}': {ex.Message}");
            }
            return loads;
        }

        private static (int[] color, string name, bool? atDestination) GetDestinationInfo(Car car)
        {
            try
            {
                var opsController = OpsController.Shared;
                if (opsController == null || !car.Waybill.HasValue) return (null, null, null);

                OpsCarPosition destination = car.Waybill.Value.Destination;
                Area area = opsController.AreaForCarPosition(destination);
                string name = opsController.NameForPosition(destination);
                bool isAtDestination = opsController.CarsAtPosition(destination).Contains(car);

                if (area == null) return (null, name, isAtDestination);

                Color c = area.tagColor;

                // Matches the base game's own treatment (found in Map
                // Enhancer's TraincarColorUpdater coroutine): boost the raw
                // tag color to full brightness while a car is en route, and
                // dull it once the car has arrived at its destination --
                // makes "still traveling" vs. "arrived" visually obvious.
                if (isAtDestination)
                {
                    Color.RGBToHSV(c, out float h, out float s, out float v);
                    v *= 0.6f;
                    c = Color.HSVToRGB(h, s, v);
                }
                else
                {
                    float maxComponent = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                    if (maxComponent > 0.0001f && maxComponent < 0.99f)
                    {
                        float boost = 1f / maxComponent;
                        c.r *= boost;
                        c.g *= boost;
                        c.b *= boost;
                    }
                }

                int[] color =
                {
                    Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f),
                    Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f),
                    Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f)
                };
                return (color, name, isAtDestination);
            }
            catch
            {
                // Best-effort only -- destination resolution touches a lot of
                // game state we don't fully control; never let this break
                // the broadcast loop for every other car.
                return (null, null, null);
            }
        }

        // Best-effort "which region is this car in right now" -- distinct
        // from destinationName (where a car is HEADED). Used for the
        // hotbox message feed, where "where is this problem" matters more
        // than the car's waybill.
        private static string GetNearestAreaName(Car car)
        {
            try
            {
                var opsController = OpsController.Shared;
                if (opsController == null) return null;

                Area area = opsController.ClosestArea(car);
                return area != null ? area.name : null;
            }
            catch
            {
                return null;
            }
        }

        // Locomotive-only info for the engine panel: type, rated tractive
        // effort, and whole-train aggregates (combined TE, car count, gross
        // weight). Returns all-null fields for a non-locomotive car.
        //
        // Car.EnumerateCoupled() walks the FULL physically-connected consist
        // regardless of which end this car sits at or how it's oriented
        // within it (confirmed by decompiling IntegrationSet.EnumerateCoupledTo:
        // it finds the true start of the connected chain first, then walks to
        // the end) -- a single default-direction call already returns the
        // entire train, no need to also walk the opposite LogicalEnd.
        private static (string locomotiveType, float? ratedTractiveEffort, int? trainCarCount, float? trainGrossWeightTons, float? trainCombinedTractiveEffort) GetLocomotiveTrainInfo(Car car)
        {
            if (!(car is BaseLocomotive loco)) return (null, null, null, null, null);

            string type = car.Archetype == CarArchetype.LocomotiveSteam ? "Steam"
                : car.Archetype == CarArchetype.LocomotiveDiesel ? "Diesel"
                : null;

            float ratedTE = loco.RatedTractiveEffort;

            int carCount = 0;
            float grossWeightLbs = 0f;
            float combinedTE = 0f;
            try
            {
                foreach (var member in car.EnumerateCoupled())
                {
                    if (member == null) continue;

                    grossWeightLbs += member.Weight;

                    if (member.IsLocomotive)
                    {
                        if (member is BaseLocomotive memberLoco)
                        {
                            combinedTE += memberLoco.RatedTractiveEffort;
                        }
                    }
                    else if (member.Archetype != CarArchetype.Tender)
                    {
                        carCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not enumerate coupled train for '{GetStableCarId(car)}': {ex.Message}");
            }

            // Car.Weight is in pounds (confirmed: the game's own GravityForce
            // computation divides by 2000 to get tons) -- 2000 lbs/ton.
            return (type, ratedTE, carCount, grossWeightLbs / 2000f, combinedTE);
        }

        // Resolves this locomotive's Auto Engineer destinations to map
        // positions: the base game's own single Orders.Waypoint (native to
        // every locomotive, no mod required) plus, if the third-party
        // Waypoint Queue mod is installed, its full per-locomotive queue
        // (see WaypointQueueBridge). Both use the same Graph.GetPositionRotation(
        // Location).Position resolution TrackExtractor uses for track
        // sampling -- world space, not WorldTransformer.WorldToGame -- since
        // Location is a track-graph concept, not a live transform position.
        private static (WaypointDto autoEngineerWaypoint, List<WaypointDto> queuedWaypoints) GetLocomotiveWaypoints(Car car, Graph graph, string carId)
        {
            WaypointDto autoEngineerWaypoint = null;
            try
            {
                if (graph != null)
                {
                    Orders orders = new AutoEngineerPersistence(car.KeyValueObject).Orders;
                    if (orders.Mode == AutoEngineerMode.Waypoint && orders.Waypoint.HasValue)
                    {
                        Location location = graph.ResolveLocationString(orders.Waypoint.Value.LocationString);
                        Vector3 pos = graph.GetPositionRotation(location).Position;
                        autoEngineerWaypoint = new WaypointDto
                        {
                            label = "Waypoint",
                            statusLabel = "Waypoint",
                            position = new float[] { pos.x, MapCoordinates.MapZ(pos.z) }
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not read Auto Engineer waypoint for '{carId}': {ex.Message}");
            }

            var queuedWaypoints = new List<WaypointDto>();
            try
            {
                if (graph != null)
                {
                    // WaypointQueue's LocoWaypointState.LocomotiveId is
                    // resolved via TrainController.Shared.TryGetCarForId,
                    // which looks up its internal _carLookup dictionary --
                    // confirmed (via decompiling) keyed by car.id, NOT the
                    // Ident-formatted carId (e.g. "UP 4014") used everywhere
                    // else in this file for our own DTOs/logging. Passing
                    // carId here instead of car.id would make every lookup
                    // silently miss.
                    foreach (var (name, statusLabel, actions, location) in WaypointQueueBridge.GetQueuedWaypoints(car.id))
                    {
                        Vector3 pos = graph.GetPositionRotation(location).Position;
                        queuedWaypoints.Add(new WaypointDto
                        {
                            label = name,
                            statusLabel = statusLabel,
                            actions = actions,
                            position = new float[] { pos.x, MapCoordinates.MapZ(pos.z) }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not resolve WaypointQueue positions for '{carId}': {ex.Message}");
            }

            return (autoEngineerWaypoint, queuedWaypoints);
        }

        internal static string GetStableCarId(Car car)
        {
            // CarIdent is a non-nullable struct; ToString() formats the road name & number.
            string carId = car.Ident.ToString();
            // car.id is the game's own persistent identifier -- a more stable
            // fallback than the Unity instance name if Ident is ever blank.
            return !string.IsNullOrEmpty(carId) ? carId : car.id;
        }

        #region Switch Events

        // Toggles a switch's thrown state, mirroring exactly what the game's
        // own UI does for a plain left-click on a switch marker
        // (JunctionMarker.OnMapMarkerPressed's else branch). Deliberately
        // refuses CTC-controlled switches -- those are locked out at the
        // track and can only be set via the CTC panel in-game, so allowing
        // a direct toggle here would let the companion app do something the
        // real game UI wouldn't allow at that same switch.
        private void HandleSetSwitchCommand(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return;

            Graph graph = Graph.Shared;
            if (graph == null)
            {
                Log.Warning($"SET_SWITCH request for '{nodeId}' ignored -- Graph not available.");
                return;
            }

            TrackNode node = graph.GetNode(nodeId);
            if (node == null)
            {
                Log.Warning($"SET_SWITCH request for unknown node '{nodeId}'.");
                return;
            }

            if (node.IsCTCSwitch)
            {
                Log.Info($"SET_SWITCH request for '{nodeId}' ignored -- CTC-controlled, must be set via the CTC panel.");
                return;
            }

            try
            {
                StateManager.ApplyLocal(new RequestSetSwitch(node.id, !node.isThrown));
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to toggle switch '{nodeId}': {ex.Message}");
            }
        }

        private void SubscribeToSwitchEvents(List<TrackNode> switchNodes)
        {
            foreach (var node in switchNodes)
            {
                if (node == null || _switchHandlers.ContainsKey(node)) continue;

                Action handler = () => OnSwitchThrownChanged(node);
                node.OnDidChangeThrown += handler;
                _switchHandlers[node] = handler;
            }
        }

        private void OnSwitchThrownChanged(TrackNode node)
        {
            if (node == null || !HasConnectedClients()) return;

            var update = new SwitchStateUpdateDto
            {
                id = !string.IsNullOrEmpty(node.id) ? node.id : node.name,
                isThrown = node.isThrown
            };

            Broadcast(JsonConvert.SerializeObject(update));
        }

        private void UnsubscribeFromSwitchEvents()
        {
            foreach (var kvp in _switchHandlers)
            {
                if (kvp.Key != null)
                {
                    kvp.Key.OnDidChangeThrown -= kvp.Value;
                }
            }
            _switchHandlers.Clear();
        }

        #endregion

        #region Camera Warp

        // Teleports whichever in-game camera is currently active (free/
        // "Strategy" camera, or the first-person character) to a map
        // location the player picked via right-click/long-press on the
        // companion app. `CameraSelector.JumpToPoint(gamePoint, rotation,
        // cameraIdentifier: null)` (confirmed by decompiling) does exactly
        // this dispatch itself -- passing null for the identifier makes it
        // use whatever mode is currently active (falling back to Strategy
        // if the player happens to be in Dispatcher mode), so this project
        // doesn't need its own logic to detect which camera is active.
        //
        // payload is "gameX,gameZ" (invariant-culture floats) -- the same
        // game-space X/Z convention every other position in this file uses,
        // EXCEPT the client's Z is negated for map display (see
        // MapCoordinates.MapZ), so it must be un-negated here before use.
        private void HandleWarpCameraCommand(string payload)
        {
            string[] parts = payload?.Split(',');
            if (parts == null || parts.Length != 2
                || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float gameX)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float clientZ))
            {
                Log.Warning($"WARP_CAMERA request ignored -- malformed payload '{payload}'.");
                return;
            }
            float gameZ = -clientZ;

            CameraSelector selector = CameraSelector.shared;
            if (selector == null)
            {
                Log.Warning("WARP_CAMERA request ignored -- CameraSelector not available.");
                return;
            }

            try
            {
                // Physics.Raycast operates in true Unity world space, but
                // the X/Z we have are in the game's floating-origin "game
                // space" -- WorldTransformer.GameToWorld/WorldToGame is a
                // simple, Y-independent offset (confirmed by decompiling:
                // literally `worldPosition +/- _currentOffset`), so a
                // placeholder Y here doesn't affect the resulting world X/Z.
                Vector3 worldGuess = WorldTransformer.GameToWorld(new Vector3(gameX, 0f, gameZ));

                // Straight-down raycast against Terrain+Track to find actual
                // ground height at that X/Z, the same pattern the game's own
                // code uses for ground-snapping (e.g. track-carving tools).
                // Matters for the first-person character: PlayerController.
                // JumpTo is a direct KinematicCharacterMotor.
                // SetPositionAndRotation teleport with no ground-snap of its
                // own (confirmed by decompiling) -- an inaccurate Y here
                // would spawn the player floating or clipped into the
                // ground. The free/Strategy camera doesn't actually need
                // this (StrategyCameraController.JumpTo re-snaps to ground
                // itself), so this is harmless -- just unnecessary -- there.
                float groundY = 0f;
                Vector3 rayOrigin = new Vector3(worldGuess.x, 2000f, worldGuess.z);
                if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 4000f, (1 << Layers.Terrain) | (1 << Layers.Track)))
                {
                    groundY = hit.point.y;
                }
                else
                {
                    Log.Warning($"WARP_CAMERA: no ground found under game ({gameX:F1}, {gameZ:F1}) -- warping at world Y=0.");
                }

                // Small clearance above the raycast-hit surface so the
                // first-person character doesn't spawn clipped into it.
                Vector3 targetWorld = new Vector3(worldGuess.x, groundY + 1.0f, worldGuess.z);
                Vector3 targetGame = WorldTransformer.WorldToGame(targetWorld);

                selector.JumpToPoint(targetGame, Quaternion.identity);
            }
            catch (Exception ex)
            {
                Log.Warning($"WARP_CAMERA request failed: {ex.Message}");
            }
        }

        #endregion

        #region Signal Events

        private SignalStorage GetSignalStorage()
        {
            if (_signalStorage != null) return _signalStorage;

            // Direct comparison, not ?. -- Unity overrides ==/!= to treat a
            // destroyed object as null, but the ?. operator bypasses that
            // override and would call GetComponentInParent on a genuinely
            // destroyed native object if Shared is a stale reference left
            // over from a previous save (CTCPanelController has no OnDestroy
            // cleanup to null out Shared itself).
            CTCPanelController controller = CTCPanelController.Shared;
            if (controller == null) return null;

            _signalStorage = controller.GetComponentInParent<SignalStorage>();
            return _signalStorage;
        }

        private void SubscribeToSignalEvents(List<CTCSignal> signals)
        {
            var storage = GetSignalStorage();
            if (storage == null) return; // e.g. signals not unlocked yet this session

            // ABS/CTC mode switches can change a switch's IsCTCSwitch status
            // without that triggering Graph's own rebuild event -- subscribe
            // once so mode changes force a fresh track cache automatically,
            // instead of requiring a manual "Refresh Track Data" click.
            if (_systemModeObserver == null)
            {
                _systemModeObserver = storage.ObserveSystemMode(OnSystemModeChanged);
            }

            foreach (var signal in signals)
            {
                if (signal == null) continue;
                string id = !string.IsNullOrEmpty(signal.id) ? signal.id : signal.name;
                if (_signalObservers.ContainsKey(id)) continue;

                _signalObservers[id] = storage.ObserveSignalAspect(id, aspect => OnSignalAspectChanged(id, aspect));
            }
        }

        private void OnSystemModeChanged(SystemMode mode)
        {
            _cachedTrackJson = null;
            _trackCacheNeedsRebroadcast = true;
        }

        private void OnSignalAspectChanged(string signalId, SignalAspect aspect)
        {
            if (!HasConnectedClients()) return;

            var update = new SignalAspectUpdateDto
            {
                id = signalId,
                aspect = aspect.ToString()
            };

            Broadcast(JsonConvert.SerializeObject(update));
        }

        private void UnsubscribeFromSignalEvents()
        {
            foreach (var observer in _signalObservers.Values)
            {
                observer?.Dispose();
            }
            _signalObservers.Clear();
        }

        #endregion

        private void OnDestroy()
        {
            Log.Info($"{PluginName} shutting down (OnDestroy)...");
            StopServer();
        }

        #region HTTP + WebSocket Server

        private byte[] _companionAppHtml;

        private void StartServer()
        {
            try
            {
                _companionAppHtml = LoadEmbeddedCompanionAppHtml();
                StartHttpListener();
                StartWebSocketServer();

                LogConnectionAddresses();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to start server: {ex}");
            }
        }

        // Shared by the startup log and the future in-game console command,
        // so both always report identical, up-to-date addresses rather than
        // two separately-maintained copies of the same logic.
        // Shared by the startup log and the in-game console command, so both
        // always report identical, up-to-date addresses rather than two
        // separately-maintained copies of the same logic.
        private List<string> BuildConnectionAddressLines()
        {
            var lines = new List<string>
            {
                "Minimap server ready. Open one of these in a browser:",
                $"  http://localhost:{HttpPort}/   (this PC)"
            };
            foreach (string ip in GetLocalIPv4Addresses())
            {
                lines.Add($"  http://{ip}:{HttpPort}/   (from another device on this network)");
            }
            return lines;
        }

        private void LogConnectionAddresses()
        {
            foreach (string line in BuildConnectionAddressLines())
            {
                Log.Info(line);
            }
        }

        // UI.Console.* -- fully qualified throughout, since Console's bare
        // name collides with System.Console (this file has `using System;`).
        //
        // This used to just listen to Console.OnUserInput, a raw firehose
        // of every line typed (not a structured command system), and react
        // to "minimap"/"/minimap" text directly. That had two real bugs:
        // typing the command WITHOUT a leading "/" also got sent as an
        // in-game chat message (the game's own OnUserInput handler treats
        // any non-"/"-prefixed line as chat via StateManager.ApplyLocal(new
        // Say(...))), and typing it WITH a leading "/" still printed the
        // game's own "Command not recognized." first, since the game's
        // handler runs its full command lookup regardless of what other
        // listeners on the same event choose to do with that text -- there's
        // no way to "consume" the event to stop it. Properly registering a
        // real UI.Console.IConsoleCommand fixes both: it only ever matches
        // an actual "/minimap" (never bare text, never as chat), and the
        // game's own dispatcher finds it directly instead of falling
        // through to "not recognized" first.
        private void TryHookConsole()
        {
            UI.Console.Console console = UI.Console.Console.shared;
            if (console == null) return; // Console UI may not exist yet; retried every tick until it does

            _consoleHooked = true; // one attempt regardless of outcome -- console exists now, stop retrying either way

            if (!TryRegisterConsoleCommand(console))
            {
                Log.Warning("Could not register the /minimap console command (game update likely changed UI.Console.ConsoleCommandHandler's internals) -- connection URLs are still logged on startup, just not available via the in-game console.");
            }
        }

        // ConsoleCommandHandler auto-discovers [ConsoleCommand]-attributed
        // commands via Assembly.GetExecutingAssembly().GetTypes() (confirmed
        // by decompiling) -- which only ever means the GAME's own assembly,
        // never this mod's, so that attribute alone can't get our command
        // registered no matter what. Instead, this reaches its private
        // `_commands` field (a plain Dictionary<string, IConsoleCommand>)
        // via reflection and inserts directly -- the exact same dictionary
        // its own commands populate themselves into via Register<T>, just
        // reached from outside. Once we have that live reference, no further
        // reflection is needed; it's an ordinary dictionary from there.
        private bool TryRegisterConsoleCommand(UI.Console.Console console)
        {
            try
            {
                var handler = console.GetComponent<UI.Console.ConsoleCommandHandler>();
                if (handler == null) return false;

                var commandsField = typeof(UI.Console.ConsoleCommandHandler)
                    .GetField("_commands", BindingFlags.NonPublic | BindingFlags.Instance);
                if (!(commandsField?.GetValue(handler) is Dictionary<string, UI.Console.IConsoleCommand> commands))
                {
                    return false;
                }

                var command = new MinimapConsoleCommand(this);
                // "/minimap_url" kept as an alias for anyone used to the old
                // bare-word version's second name.
                commands["/minimap"] = command;
                commands["/minimap_url"] = command;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to register /minimap console command via reflection: {ex.Message}");
                return false;
            }
        }

        private void TryUnregisterConsoleCommand()
        {
            try
            {
                UI.Console.Console console = UI.Console.Console.shared;
                var handler = console?.GetComponent<UI.Console.ConsoleCommandHandler>();
                if (handler == null) return;

                var commandsField = typeof(UI.Console.ConsoleCommandHandler)
                    .GetField("_commands", BindingFlags.NonPublic | BindingFlags.Instance);
                if (commandsField?.GetValue(handler) is Dictionary<string, UI.Console.IConsoleCommand> commands)
                {
                    commands.Remove("/minimap");
                    commands.Remove("/minimap_url");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to unregister /minimap console command: {ex.Message}");
            }
        }

        // A properly-registered slash command rather than the raw text
        // listener this used to be -- see TryHookConsole above for why.
        // Nested privately since it only exists to bridge into
        // ConsoleCommandHandler's dictionary and has no reason to be
        // constructed from anywhere else. [ConsoleCommand] is still applied
        // here (even though attribute-based auto-discovery can't see it,
        // being in the wrong assembly) because UI.Console.Console's own
        // /help command reads a command's name/description straight off
        // this same attribute via reflection -- without it, /help would
        // throw a NullReferenceException the moment it reached this entry.
        [UI.Console.ConsoleCommand("/minimap", "Show the Minimap Server's connection URLs.")]
        private class MinimapConsoleCommand : UI.Console.IConsoleCommand
        {
            private readonly MinimapServerCore _core;

            public MinimapConsoleCommand(MinimapServerCore core)
            {
                _core = core;
            }

            public string Execute(string[] components)
            {
                return string.Join("\n", _core.BuildConnectionAddressLines());
            }
        }

        private void StartHttpListener()
        {
            _httpCts = new CancellationTokenSource();
            _httpListener = new HttpListener();

            // Explicit per-address prefixes (not a "+"/"*" wildcard) --
            // Windows only requires admin/URL ACL registration for wildcard
            // HttpListener bindings. Binding each known address individually
            // sidesteps that entirely.
            _httpListener.Prefixes.Add($"http://127.0.0.1:{HttpPort}/");
            _httpListener.Prefixes.Add($"http://localhost:{HttpPort}/");
            foreach (string ip in GetLocalIPv4Addresses())
            {
                _httpListener.Prefixes.Add($"http://{ip}:{HttpPort}/");
            }

            _httpListener.Start();
            Task.Run(() => HttpAcceptLoopAsync(_httpCts.Token));
        }

        private async Task HttpAcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    if (!_httpListener.IsListening) break;
                    ctx = await _httpListener.GetContextAsync();
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex)
                {
                    Log.Warning($"HTTP listener error: {ex.Message}");
                    continue;
                }

                try
                {
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentEncoding = Encoding.UTF8;
                    // No cache headers were set here at all previously --
                    // the browser was free to serve a stale cached copy of
                    // this page indefinitely on ordinary reload/reconnect
                    // (only a hard refresh would bypass it), which is
                    // exactly the kind of thing that makes "I fixed this
                    // already" bug reports genuinely confusing to debug.
                    // This mod's own DLL always serves whatever HTML is
                    // currently embedded in it, so there's never a reason
                    // for a client to keep an old copy around.
                    ctx.Response.Headers.Add("Cache-Control", "no-store, no-cache, must-revalidate");
                    ctx.Response.Headers.Add("Pragma", "no-cache");
                    ctx.Response.ContentLength64 = _companionAppHtml.Length;
                    ctx.Response.OutputStream.Write(_companionAppHtml, 0, _companionAppHtml.Length);
                    ctx.Response.OutputStream.Close();
                }
                catch (Exception ex)
                {
                    Log.Warning($"Error writing HTTP response: {ex.Message}");
                }
            }
        }

        private void StartWebSocketServer()
        {
            // IPAddress.Any (not Loopback) -- this needs to be reachable from
            // other devices on the LAN (phone/tablet as a true companion
            // device), not just this PC. Anyone on the same network can
            // connect; acceptable for a home network, but worth knowing.
            _wsServer = new WebSocketServer(IPAddress.Any, WsPort);
            _wsServer.AddWebSocketService<MinimapSocketBehavior>(ServicePath, () => new MinimapSocketBehavior(_inboundQueue));
            _wsServer.Start();
        }

        private byte[] LoadEmbeddedCompanionAppHtml()
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            // Resource name is "<DefaultNamespace>.<FileName>" for a plain
            // EmbeddedResource with no folder structure.
            string resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("CompanionApp.html", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                Log.Error("CompanionApp.html embedded resource not found. Available resources: " +
                    string.Join(", ", assembly.GetManifestResourceNames()));
                return Encoding.UTF8.GetBytes("<html><body>Companion app resource missing from this build.</body></html>");
            }

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return Encoding.UTF8.GetBytes(reader.ReadToEnd());
            }
        }

        private static IEnumerable<string> GetLocalIPv4Addresses()
        {
            var results = new List<string>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                    foreach (var addrInfo in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addrInfo.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            results.Add(addrInfo.Address.ToString());
                        }
                    }
                }
            }
            catch
            {
                // Best-effort only -- if this fails, the user still has the
                // localhost URL logged above.
            }
            return results;
        }

        private bool HasConnectedClients()
        {
            var host = GetServiceHost();
            return host != null && host.Sessions.Count > 0;
        }

        private void Broadcast(string json)
        {
            var host = GetServiceHost();
            if (host == null) return;

            try
            {
                host.Sessions.Broadcast(json);
            }
            catch (Exception ex)
            {
                Log.Warning($"Broadcast failed: {ex.Message}");
            }
        }

        private WebSocketServiceHost GetServiceHost()
        {
            if (_wsServer == null || !_wsServer.IsListening) return null;
            return _wsServer.WebSocketServices[ServicePath];
        }

        private void StopServer()
        {
            UnsubscribeFromSwitchEvents();
            UnsubscribeFromSignalEvents();
            _systemModeObserver?.Dispose();
            _systemModeObserver = null;
            Messenger.Default.Unregister<GraphDidRebuildCollections>(this);

            if (_consoleHooked)
            {
                TryUnregisterConsoleCommand();
                _consoleHooked = false;
            }

            try
            {
                // UnpatchAll(string) -- the older, more fundamental Harmony
                // API, compatible with the Harmony version UMM bundles.
                _harmony?.UnpatchAll(PluginGuid);
            }
            catch (Exception ex)
            {
                Log.Warning($"Error unpatching Harmony: {ex.Message}");
            }

            try
            {
                _httpCts?.Cancel();
                if (_httpListener != null && _httpListener.IsListening)
                {
                    _httpListener.Stop();
                    _httpListener.Close();
                }
                _httpCts?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning($"Error stopping HTTP listener: {ex.Message}");
            }

            try
            {
                _wsServer?.Stop();
            }
            catch (Exception ex)
            {
                Log.Warning($"Error stopping WebSocket server: {ex.Message}");
            }
        }

        #endregion
    }

    // Captures the same live position/rotation the game's own minimap uses
    // for each car, by patching Car.UpdateMapIconPosition -- discovered by
    // examining how the Map Enhancer mod reliably tracks cars (it uses
    // TrainController.Shared.Cars + this same hook, rather than scanning the
    // scene directly). This fires for every car regardless of whether its
    // visual model is currently loaded, which a raw FindObjectsOfType<Car>()
    // scan and BodyTransform read could not reliably provide.
    //
    // This is a Postfix -- purely observational, runs after the original
    // method, and never alters its behavior or the game's own minimap.
    [HarmonyPatch(typeof(Car), "UpdateMapIconPosition")]
    internal static class Car_UpdateMapIconPosition_Patch
    {
        public static readonly ConcurrentDictionary<string, (Vector3 position, Quaternion rotation)> LatestPositions
            = new ConcurrentDictionary<string, (Vector3, Quaternion)>();

        private static void Postfix(Car __instance, Vector3 position, Quaternion rotation)
        {
            if (__instance == null) return;
            string carId = MinimapServerCore.GetStableCarId(__instance);
            if (string.IsNullOrEmpty(carId)) return;

            LatestPositions[carId] = (position, rotation);
        }
    }

    // Handles per-connection events on WebSocketSharp's own internal thread(s)
    // -- NOT the Unity main thread. Keep this minimal: just forward inbound
    // messages to the plugin's queue so they're processed during Update()
    // like everything else that touches Unity/game state.
    public class MinimapSocketBehavior : WebSocketBehavior
    {
        private readonly ConcurrentQueue<string> _inboundQueue;

        public MinimapSocketBehavior(ConcurrentQueue<string> inboundQueue)
        {
            _inboundQueue = inboundQueue;
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            _inboundQueue.Enqueue(e.Data);
        }
    }
}
