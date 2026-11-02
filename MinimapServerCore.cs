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
using Game.Events; // EventBus, GraphDidRebuildCollections
using Game.Messages; // RequestSetSwitch
using Game.Mow; // TrackConditionController (Maintenance of Way)
using Game.State; // StateManager
using UnityEngine;
using Newtonsoft.Json;
using Model; // Railroader's vehicle namespace (Car, Locomotive, CarType)
using Model.AI; // AutoEngineerPersistence, Orders, AutoEngineerMode
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
    // (GameModEntry.cs handles logging setup and instantiation) so the two
    // concerns don't tangle together.
    public class MinimapServerCore : MonoBehaviour
    {
        public const string PluginName = "Railroader Minimap Server";
        public const string PluginVersion = "0.10.0";

        // The single live instance, for the console command class (which the
        // game constructs itself via Activator.CreateInstance, so it can't be
        // handed a reference any other way).
        internal static MinimapServerCore Instance { get; private set; }

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
        // only as a final fallback if a car has neither a valid track
        // Location nor a loaded body (e.g. in the first instant after it's
        // created).
        private readonly Dictionary<string, CarDataDto> _lastKnownState = new Dictionary<string, CarDataDto>();

        // Handlers registered against TrackNode.OnDidChangeThrown, kept so we
        // can unsubscribe cleanly on shutdown/reload.
        private readonly Dictionary<TrackNode, Action> _switchHandlers = new Dictionary<TrackNode, Action>();

        // SignalStorage isn't a global singleton itself -- reached via
        // CTCPanelController.Shared.GetComponentInParent<SignalStorage>(),
        // same instance the whole CTC system uses internally.
        private SignalStorage _signalStorage;

        // The console whose command handler /minimap is currently registered
        // with (see TryHookConsole). Resolved lazily, since the Console UI may
        // not exist yet the moment this mod loads, and re-checked every tick
        // in case the game recreates it (a fresh ConsoleCommandHandler starts
        // with only the game's own commands).
        private UI.Console.Console _hookedConsole;
        private bool _consoleHookFailed = false;
        private IDisposable _systemModeObserver;
        private readonly Dictionary<string, IDisposable> _signalObservers = new Dictionary<string, IDisposable>();

        private void Awake()
        {
            Instance = this;
            Log.Info($"{PluginName} {PluginVersion} initializing...");

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
            // rest of the session. (Railroader 2026.1 replaced MVVM Light's
            // Messenger.Default with its own Game.Events.EventBus.)
            EventBus.Subscribe<GraphDidRebuildCollections>(this, OnGraphRebuilt);

            StartServer();
        }

        // Called by GameModEntry via the game's IWorldLifecycle hooks. Every
        // reference into the previous world's objects (switch nodes, the
        // SignalStorage, the MoW controller, cached geometry) is dropped
        // here, since loading a different save would otherwise leave them
        // pointing at destroyed objects from the old one.
        internal void OnWorldUnloaded()
        {
            UnsubscribeFromSwitchEvents();
            UnsubscribeFromSignalEvents();
            _systemModeObserver?.Dispose();
            _systemModeObserver = null;
            _signalStorage = null;
            _cachedTrackJson = null;
            _lastKnownState.Clear();
            ResetTrackConditionState();
        }

        internal void OnWorldLoaded()
        {
            _cachedTrackJson = null;
            _trackCacheNeedsRebroadcast = true;
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
            if (!_consoleHookFailed) TryHookConsole();

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
                        // Track condition is keyed by segment id, so it's
                        // only meaningful after the client has the matching
                        // network -- always follow it with a full snapshot.
                        BroadcastTrackCondition(fullSnapshot: true);
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
                        BroadcastTrackCondition(fullSnapshot: true);
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

            // 3. Maintenance of Way track condition -- changes slowly (wear
            // accumulates with traffic; maintenance/construction happen over
            // in-game hours), so a much coarser poll than car telemetry,
            // sending only segments whose condition actually changed.
            _trackConditionTimer += Time.deltaTime;
            if (_trackConditionTimer >= TrackConditionPollInterval)
            {
                _trackConditionTimer = 0f;
                if (_cachedTrackJson != null)
                {
                    BroadcastTrackCondition(fullSnapshot: false);
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

            int total = 0, viaGraph = 0, viaBodyFallback = 0, lastKnownFallback = 0, skipped = 0;

            foreach (var car in controller.Cars)
            {
                if (car == null) continue;
                total++;

                if (graph != null && car.LocationF.IsValid && car.LocationR.IsValid)
                {
                    viaGraph++;
                }
                else if (car.BodyTransform != null)
                {
                    viaBodyFallback++;
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
                $"totalCars={total}, viaGraph={viaGraph}, viaBodyFallback={viaBodyFallback}, " +
                $"lastKnownFallback={lastKnownFallback}, skippedEntirely={skipped}");
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
                        : car.CarType.ToString();
                    // The base game excludes passenger cars from destination
                    // coloring entirely (see IsFreight check in the
                    // TraincarColorUpdater coroutine) -- Coach/Baggage are
                    // the passenger-carrying types. Railroader 2026.1 removed
                    // the CarArchetype enum; Car.CarType is now a struct with
                    // prefix-based helpers (IsCoach = "PB", IsTender = "LT").
                    isPassenger = car.CarType.IsCoach || car.CarType.IsBaggage;
                    isTender = car.CarType.IsTender;
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
                        if (tender == null || !tender.CarType.IsTender)
                        {
                            tender = car.CoupledTo(Car.LogicalEnd.B);
                        }
                        if (tender != null && tender.CarType.IsTender)
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
                (string locomotiveType, float? ratedTractiveEffort, int? trainCarCount, float? consistWeightTons, float? trainCombinedTractiveEffort) =
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
                    // Preferred over the body-transform fallback below: that
                    // only exists while the car's visual model is loaded, and
                    // is in true Unity world space, which the game rebases
                    // underneath it (floating origin -- CarMover.WorldDidMove/
                    // "MoveWorld" logs). Location-based position is never
                    // cached and works for unloaded cars too.
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
                        consistWeightTons = consistWeightTons,
                        trainCombinedTractiveEffort = trainCombinedTractiveEffort,
                        autoEngineerWaypoint = autoEngineerWaypoint,
                        queuedWaypoints = queuedWaypoints
                    };
                    _lastKnownState[carId] = dto;
                }
                else if (car.BodyTransform != null)
                {
                    // Fallback for the rare case a car doesn't have a valid
                    // track Location yet (e.g. very early in setup) but its
                    // body is already placed. This used to come from a Harmony
                    // postfix on Car.UpdateMapIconPosition, but that method is
                    // handed exactly the position the body is moved to, and
                    // dropping Harmony means the mod needs nothing beyond what
                    // the game itself ships (Harmony only exists in a game
                    // install when another mod loader put it there). Still
                    // needs the game-space conversion -- world space here.
                    Transform body = car.BodyTransform;
                    Vector3 gamePos = WorldTransformer.WorldToGame(body.position);
                    dto = new CarDataDto
                    {
                        id = carId,
                        x = gamePos.x,
                        z = MapCoordinates.MapZ(gamePos.z),
                        rotationY = body.rotation.eulerAngles.y,
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
                        consistWeightTons = consistWeightTons,
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

                        // CarLoadLibrary replaced CarPrototypeLibrary.LoadForId
                        // in Railroader 2026.1 (returns null for an unknown id).
                        Load load = CarLoadLibrary.GetLoad(loadInfo.Value.LoadId);
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
        // effort, and whole-train aggregates (combined TE, car count,
        // consist weight). Returns all-null fields for a non-locomotive car.
        //
        // Car.EnumerateCoupled() walks the FULL physically-connected consist
        // regardless of which end this car sits at or how it's oriented
        // within it (confirmed by decompiling IntegrationSet.EnumerateCoupledTo:
        // it finds the true start of the connected chain first, then walks to
        // the end) -- a single default-direction call already returns the
        // entire train, no need to also walk the opposite LogicalEnd.
        private static (string locomotiveType, float? ratedTractiveEffort, int? trainCarCount, float? consistWeightTons, float? trainCombinedTractiveEffort) GetLocomotiveTrainInfo(Car car)
        {
            if (!(car is BaseLocomotive loco)) return (null, null, null, null, null);

            string type = car.CarType.IsLocomotiveSteam ? "Steam"
                : car.CarType.IsLocomotiveDiesel ? "Diesel"
                : null;

            float ratedTE = loco.RatedTractiveEffort;

            // The consist: this engine, its tender, and everything coupled to
            // them, each counted once. EnumerateCoupled() already includes
            // the engine itself and its tender (it's the same walk the game's
            // own HUD tonnage uses), but they're added explicitly too so the
            // engine and tender always count even if that walk fails or
            // returns nothing (e.g. a car with no integration set yet).
            var consist = new HashSet<Car> { car };
            try
            {
                Car tender = car.CoupledTo(Car.LogicalEnd.A);
                if (tender == null || !tender.CarType.IsTender) tender = car.CoupledTo(Car.LogicalEnd.B);
                if (tender != null && tender.CarType.IsTender) consist.Add(tender);

                foreach (var member in car.EnumerateCoupled())
                {
                    if (member != null) consist.Add(member);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not enumerate coupled train for '{GetStableCarId(car)}': {ex.Message}");
            }

            int carCount = 0;
            float consistWeightLbs = 0f;
            float combinedTE = 0f;
            try
            {
                foreach (var member in consist)
                {
                    // Car.Weight is empty weight plus every load slot, and a
                    // locomotive's or tender's fuel, water, and coal are load
                    // slots like any other cargo -- so this already includes
                    // them (decompiled: Weight => WeightEmpty + _loadWeight,
                    // summed over all LoadSlots via Load.Pounds).
                    consistWeightLbs += member.Weight;

                    if (member.IsLocomotive)
                    {
                        if (member is BaseLocomotive memberLoco)
                        {
                            combinedTE += memberLoco.RatedTractiveEffort;
                        }
                    }
                    else if (!member.CarType.IsTender)
                    {
                        carCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not total consist for '{GetStableCarId(car)}': {ex.Message}");
            }

            // Car.Weight is in pounds; 2000 lbs/ton. Rounded up to whole
            // tons from the summed pounds, exactly like the game's own HUD
            // ("cars, tons" label -- LocomotiveControlsHoverArea.
            // CalculateTonnage), so the two always show the same number.
            return (type, ratedTE, carCount, Mathf.Ceil(consistWeightLbs / 2000f), combinedTE);
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
                id = node.id,
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

        #region Maintenance of Way (track condition)

        // Railroader 2026.1's Maintenance of Way: every track segment wears
        // with traffic, and worn track is posted down to a lower class (a
        // speed restriction) until a work crew maintains it. New track is
        // also built segment by segment now, so a segment can exist in the
        // Graph long before it's usable. All of this is read through
        // Game.Mow.TrackConditionController's public per-segment getters.

        private float _trackConditionTimer = 0f;
        private const float TrackConditionPollInterval = 5.0f;

        // Last values sent per segment id, so a poll only broadcasts segments
        // that actually changed. Cleared whenever a full snapshot is sent.
        private readonly Dictionary<string, int[]> _sentTrackCondition = new Dictionary<string, int[]>();
        private bool _trackConditionErrorLogged = false;

        private void ResetTrackConditionState()
        {
            _sentTrackCondition.Clear();
            _trackConditionTimer = 0f;
        }

        private void BroadcastTrackCondition(bool fullSnapshot)
        {
            // Nobody to send to: skip the work entirely. A newly connecting
            // client always asks for GET_TRACK_DATA, which sends a full
            // snapshot, so no deltas need to accumulate in the meantime.
            if (!HasConnectedClients()) return;

            Graph graph = Graph.Shared;
            if (graph == null || !graph.HasPopulatedCollections) return;

            // Direct == null comparison (Unity destroyed-object semantics),
            // not ?. -- see the CTCPanelController note in GetSignalStorage.
            TrackConditionController controller = TrackConditionController.Shared;
            if (controller == null) return;

            if (fullSnapshot) _sentTrackCondition.Clear();
            var update = new TrackConditionUpdateDto { full = fullSnapshot };

            foreach (var segment in graph.Segments)
            {
                // Turntable bridges aren't right-of-way: MoW never wears or
                // builds them, and the controller reports them as pristine.
                if (segment == null || segment.a == null || segment.b == null || !segment.IsRightOfWay) continue;

                int[] values;
                try
                {
                    values = ReadTrackCondition(controller, segment);
                }
                catch (Exception ex)
                {
                    // Once, not once per segment per poll -- if one throws,
                    // likely all do (e.g. the controller isn't set up yet).
                    if (!_trackConditionErrorLogged)
                    {
                        _trackConditionErrorLogged = true;
                        Log.Warning($"Could not read track condition (further errors suppressed): {ex.Message}");
                    }
                    continue;
                }

                string id = TrackExtractor.SegmentIdentifier(segment);
                if (!fullSnapshot && _sentTrackCondition.TryGetValue(id, out int[] previous) && previous.SequenceEqual(values)) continue;

                _sentTrackCondition[id] = values;
                update.segments[id] = values;
            }

            if (!fullSnapshot && update.segments.Count == 0) return;
            Broadcast(JsonConvert.SerializeObject(update));
        }

        // See TrackConditionUpdateDto for the array layout.
        private static int[] ReadTrackCondition(TrackConditionController controller, TrackSegment segment)
        {
            float condition = controller.GetCondition(segment);           // 0-1
            int gradingClass = Mathf.Clamp(controller.GetGradingClass(segment), 1, 5);
            int postedClass = Mathf.Clamp(segment.PostedTrackClass, 1, 5);
            TrackConstructionState state = segment.ConstructionState;
            int progressPercent = state == TrackConstructionState.Complete
                ? 100
                : Mathf.Clamp(Mathf.RoundToInt(controller.GetConstructionProgressByPhase(segment) * 100f), 0, 100);

            return new[] { ConditionPercent(gradingClass, condition), gradingClass, postedClass, (int)state, progressPercent };
        }

        // Matches the game's own tooltip text exactly (decompiled from
        // TrackConditionModel.GetConditionDescription, which is internal):
        // condition as a fraction of the grading class's target condition
        // (class N targets N * 20%), floored, with a small tolerance so track
        // maintained exactly to its class reads 100% rather than 99%.
        private static int ConditionPercent(int gradingClass, float condition)
        {
            float target = gradingClass * 0.2f;
            if (target - condition <= 0.001f) return 100;
            return (int)Mathf.Floor(Mathf.Min(condition / target, 1f) * 100f);
        }

        #endregion

        private void OnDestroy()
        {
            Log.Info($"{PluginName} shutting down (OnDestroy)...");
            StopServer();
            if (Instance == this) Instance = null;
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
        // Railroader 2026.1 replaced the old IConsoleCommand/_commands
        // dictionary with an attribute-based system: a class marked
        // [ConsoleCommandHandler("/name", "description")], whose methods are
        // marked [ConsoleDefaultCommand] (bare "/name") or
        // [ConsoleSubcommand] ("/name sub ..."). ConsoleCommandHandler.Awake
        // only scans the game's own assembly (Assembly.GetExecutingAssembly()),
        // but the scanning itself is the public
        // CommandProcessor.RegisterHandlers(Assembly) -- so this reaches the
        // handler's private `_processor` field via reflection and hands it
        // this mod's assembly. Everything from there (dispatch, prefix
        // matching, /help listing, usage text) is the game's own code.
        //
        // Registering properly (rather than listening to Console.OnUserInput)
        // matters: a raw listener can't stop the game from also treating the
        // line as chat (no leading "/") or printing "Command not recognized."
        // (with one).
        private void TryHookConsole()
        {
            UI.Console.Console console = UI.Console.Console.shared;
            if (console == null) return; // Console UI may not exist yet; retried every tick until it does
            if (console == _hookedConsole) return; // already registered with this one

            if (TryRegisterConsoleCommand(console))
            {
                _hookedConsole = console;
            }
            else
            {
                // One failure is enough to know the game's internals changed;
                // don't retry (and re-log) every tick for the whole session.
                _consoleHookFailed = true;
                Log.Warning("Could not register the /minimap console command (game update likely changed UI.Console.ConsoleCommandHandler's internals) -- connection URLs are still logged on startup, just not available via the in-game console.");
            }
        }

        private static UI.Console.CommandProcessor GetCommandProcessor(UI.Console.Console console)
        {
            var handler = console.GetComponent<UI.Console.ConsoleCommandHandler>();
            if (handler == null) return null;

            var processorField = typeof(UI.Console.ConsoleCommandHandler)
                .GetField("_processor", BindingFlags.NonPublic | BindingFlags.Instance);
            return processorField?.GetValue(handler) as UI.Console.CommandProcessor;
        }

        // CommandProcessor keeps registered commands in a private
        // `_handlers` Dictionary<string, CommandHandlerInfo>, keyed by the
        // command name without its "/". CommandHandlerInfo is an internal
        // struct, so it's only reachable as a non-generic IDictionary -- which
        // is all that's needed to check for or remove one key.
        private static System.Collections.IDictionary GetRegisteredHandlers(UI.Console.CommandProcessor processor)
        {
            var handlersField = typeof(UI.Console.CommandProcessor)
                .GetField("_handlers", BindingFlags.NonPublic | BindingFlags.Instance);
            return handlersField?.GetValue(processor) as System.Collections.IDictionary;
        }

        private bool TryRegisterConsoleCommand(UI.Console.Console console)
        {
            try
            {
                var processor = GetCommandProcessor(console);
                if (processor == null) return false;

                // RegisterHandlers logs an error for a command that's already
                // registered -- e.g. after the mod is unloaded and reloaded
                // without the console itself being recreated.
                var handlers = GetRegisteredHandlers(processor);
                if (handlers != null && handlers.Contains(MinimapConsoleCommand.Name)) return true;

                processor.RegisterHandlers(typeof(MinimapConsoleCommand).Assembly);
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
                UI.Console.Console console = _hookedConsole;
                _hookedConsole = null;
                if (console == null) return;

                var processor = GetCommandProcessor(console);
                if (processor == null) return;
                GetRegisteredHandlers(processor)?.Remove(MinimapConsoleCommand.Name);
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to unregister /minimap console command: {ex.Message}");
            }
        }

        // The game constructs this itself (Activator.CreateInstance) for
        // every invocation, so it reaches the server through the static
        // Instance rather than a constructor argument. The game's dispatcher
        // only looks for non-public methods, hence `private`.
        [UI.Console.ConsoleCommandHandler("/" + Name, "Show the Minimap Server's connection URLs.")]
        private class MinimapConsoleCommand
        {
            public const string Name = "minimap";

            [UI.Console.ConsoleDefaultCommand]
            private string Show()
            {
                MinimapServerCore core = Instance;
                if (core == null) return "The Minimap Server isn't running.";
                return string.Join("\n", core.BuildConnectionAddressLines());
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
            // The game's WebSocketSharp build takes an initializer for a
            // behavior it constructs itself (new()), rather than a factory.
            _wsServer.AddWebSocketService<MinimapSocketBehavior>(ServicePath, behavior => behavior.InboundQueue = _inboundQueue);
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
            EventBus.Unsubscribe(this);
            TryUnregisterConsoleCommand();

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

    // Handles per-connection events on WebSocketSharp's own internal thread(s)
    // -- NOT the Unity main thread. Keep this minimal: just forward inbound
    // messages to the plugin's queue so they're processed during Update()
    // like everything else that touches Unity/game state.
    public class MinimapSocketBehavior : WebSocketBehavior
    {
        internal ConcurrentQueue<string> InboundQueue { get; set; }

        protected override void OnMessage(MessageEventArgs e)
        {
            InboundQueue?.Enqueue(e.Data);
        }
    }
}
