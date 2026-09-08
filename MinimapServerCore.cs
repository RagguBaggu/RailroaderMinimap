using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
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
        public const string PluginVersion = "1.0.0";

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

        // Console.OnUserInput fires for every line typed into the in-game
        // console, for any listener to interpret -- not a structured
        // registered-command system. Tracked so we only subscribe once,
        // resolved lazily since the Console UI may not exist yet the moment
        // this plugin's Awake() runs.
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
                TrackNetworkExtractionResult result = TrackExtractor.ExportActiveNetwork(sampleIntervalMeters: 6.0f);

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
                Log.Info($"Cached {result.Network.segments.Count} segments, {result.Network.switches.Count} switches, and {result.Network.signals.Count} signals.");
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

            var payload = new LiveStatePayloadDto { timestamp = Time.time };
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
                    carType = car.CarType;
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

                (int[] destColor, string destName) = isPassenger ? (null, null) : GetDestinationInfo(car);

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
                        destinationName = destName
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
                        destinationName = destName
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

        private static (int[] color, string name) GetDestinationInfo(Car car)
        {
            try
            {
                var opsController = OpsController.Shared;
                if (opsController == null || !car.Waybill.HasValue) return (null, null);

                OpsCarPosition destination = car.Waybill.Value.Destination;
                Area area = opsController.AreaForCarPosition(destination);
                string name = opsController.NameForPosition(destination);

                if (area == null) return (null, name);

                Color c = area.tagColor;

                // Matches the base game's own treatment (found in Map
                // Enhancer's TraincarColorUpdater coroutine): boost the raw
                // tag color to full brightness while a car is en route, and
                // dull it once the car has arrived at its destination --
                // makes "still traveling" vs. "arrived" visually obvious.
                bool isAtDestination = opsController.CarsAtPosition(destination).Contains(car);
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
                return (color, name);
            }
            catch
            {
                // Best-effort only -- destination resolution touches a lot of
                // game state we don't fully control; never let this break
                // the broadcast loop for every other car.
                return (null, null);
            }
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

        // UI.Console.Console -- fully qualified throughout, since its bare
        // name collides with System.Console (this file has `using System;`).
        private void TryHookConsole()
        {
            UI.Console.Console console = UI.Console.Console.shared;
            if (console == null) return; // Console UI may not exist yet; retried every tick until it does

            console.OnUserInput += OnConsoleUserInput;
            _consoleHooked = true;
        }

        private void OnConsoleUserInput(string line)
        {
            // Not a registered-command system -- OnUserInput fires for
            // every line typed into the console, for any listener
            // (including the game's own handlers) to interpret. This only
            // reacts to its own specific text and leaves everything else
            // alone, so it can't interfere with any other command.
            string trimmed = (line ?? string.Empty).Trim();
            // Accept an optional leading "/" -- HandleUserInput passes the
            // raw typed text with no visible prefix-stripping in the code
            // we've seen, but that doesn't rule out some upstream UI
            // convention we haven't looked at. Stripping it ourselves means
            // both "minimap" and "/minimap" work regardless.
            if (trimmed.StartsWith("/")) trimmed = trimmed.Substring(1);

            if (!trimmed.Equals("minimap", StringComparison.OrdinalIgnoreCase)
                && !trimmed.Equals("minimap_url", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            UI.Console.Console console = UI.Console.Console.shared;
            if (console == null) return;

            foreach (string outLine in BuildConnectionAddressLines())
            {
                console.AddLine(outLine);
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
                UI.Console.Console console = UI.Console.Console.shared;
                if (console != null)
                {
                    console.OnUserInput -= OnConsoleUserInput;
                }
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
