using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using RailroaderMinimapServer.Data;
using Helpers; // WorldTransformer.WorldToGame
using Track;
using Track.Signals; // CTCSignal, SignalAspect
using UI.Map; // MapLabel
using Model.Ops; // PassengerStop
using RollingStock; // CarLoadTargetLoader

namespace RailroaderMinimapServer
{
    // Railroader's world Z axis does not correspond to "north up" on a
    // conventional top-down map -- confirmed empirically against the base
    // game's own minimap, it needs inverting to read correctly. This is a
    // fixed property of how the base map was authored in the Unity editor,
    // not something expected to vary per session. If FUSE custom-map support
    // is ever added, a community map could in principle use a different
    // orientation convention, in which case this would need to become
    // per-map configurable rather than a flat constant.
    internal static class MapCoordinates
    {
        public static float MapZ(float worldZ) => -worldZ;
    }

    // Bundles the static DTO with the live TrackNode references identified as
    // switches, so the caller can subscribe to their OnDidChangeThrown events
    // for push-based updates instead of polling every switch on every tick.
    public class TrackNetworkExtractionResult
    {
        public TrackNetworkDto Network;
        public List<TrackNode> SwitchNodes;
        public List<CTCSignal> Signals;
    }

    public static class TrackExtractor
    {
        // Railroader has no standalone "TrackSwitch" component -- junction /
        // thrown state lives directly on TrackNode. Whether a node is an
        // actual switch (vs. a plain point joining two segments end-to-end)
        // is determined by Graph.IsSwitch, not by us.

        public static TrackNetworkExtractionResult ExportActiveNetwork(float sampleIntervalMeters = 6.0f)
        {
            var network = new TrackNetworkDto();
            var switchNodes = new List<TrackNode>();
            var signals = new List<CTCSignal>();

            // Use Graph's own already-built collections instead of scene-wide
            // FindObjectsOfType scans -- Graph maintains these itself (rebuilt
            // in RebuildCollections) and they're what the game's own switch
            // detection (IsSwitch) is defined against.
            Graph graph = Graph.Shared;
            if (graph == null || !graph.HasPopulatedCollections)
            {
                return new TrackNetworkExtractionResult { Network = network, SwitchNodes = switchNodes, Signals = signals };
            }

            // 1. EXTRACT TRACK POLYLINES
            foreach (var segment in graph.Segments)
            {
                if (segment == null || segment.a == null || segment.b == null) continue;

                var segDto = new SegmentDto
                {
                    id = SegmentIdentifier(segment),
                    nodeA = NodeIdentifier(segment.a),
                    nodeB = NodeIdentifier(segment.b),
                    length = segment.GetLength(),               // GetLength(), not a "Length" property
                    speedLimit = segment.GetExpectedSpeedLimit() // resolves the "0 = class default" case
                };

                // Sampled by DISTANCE (not curve parameter t) via TrackSegment's own
                // GetPositionRotationAtDistance -- the same method the game itself uses
                // for wheel/coupler placement. Sampling by t would space points unevenly
                // on curved track (bunched in tight curves, sparse on straights); this
                // gives evenly-spaced points at ~sampleIntervalMeters apart.
                float length = segDto.length;
                int steps = Mathf.Max(2, Mathf.CeilToInt(length / sampleIntervalMeters));

                for (int i = 0; i <= steps; i++)
                {
                    float distance = Mathf.Min(i * sampleIntervalMeters, length);
                    segment.GetPositionRotationAtDistance(
                        distance,
                        TrackSegment.End.A,
                        PositionAccuracy.Standard,
                        out Vector3 worldPos,
                        out Quaternion _);
                    segDto.points.Add(new float[] { worldPos.x, MapCoordinates.MapZ(worldPos.z) });
                }

                network.segments.Add(segDto);
            }

            // 2. IDENTIFY SWITCHES via the game's own authoritative check --
            // Graph.IsSwitch excludes turntable connections and requires
            // exactly 3 connected segments, which a naive per-segment tally
            // wouldn't replicate correctly.
            foreach (var node in graph.Nodes)
            {
                if (node == null || !graph.IsSwitch(node)) continue;

                switchNodes.Add(node);

                // IMPORTANT: TrackSegment.Curve is built from a/b's
                // transform.localPosition (see TrackSegment.CreateBezier),
                // so every point in a segment's polyline is in the LOCAL
                // coordinate space of whatever parent the track hierarchy
                // sits under -- not true Unity world space. Switch positions
                // must use the same localPosition (not .position) or they'll
                // render disconnected from the track they sit on.
                Vector3 pos = node.transform.localPosition;
                network.switches.Add(new SwitchDto
                {
                    id = NodeIdentifier(node),
                    position = new float[] { pos.x, MapCoordinates.MapZ(pos.z) },
                    isThrown = node.isThrown, // lowercase 'i' -- real member name
                    isCTC = node.IsCTCSwitch
                });
            }

            // 3. EXTRACT SIGNALS -- a completely separate object from
            // switches, each with its own position and one of six real
            // aspects (Stop/Approach/Clear/DivergingApproach/DivergingClear/
            // Restricting), not just on/off. Active-only (not
            // includeInactive): unlike cars, an inactive signal here
            // plausibly means "not yet unlocked/relevant" for this save
            // (players don't start with signals), not "streamed out for
            // performance" the way an unloaded car model is.
            foreach (var signal in UnityEngine.Object.FindObjectsOfType<CTCSignal>())
            {
                if (signal == null) continue;
                signals.Add(signal);

                Vector3 pos = WorldTransformer.WorldToGame(signal.transform.position);
                network.signals.Add(new SignalDto
                {
                    id = !string.IsNullOrEmpty(signal.id) ? signal.id : signal.name,
                    position = new float[] { pos.x, MapCoordinates.MapZ(pos.z) },
                    aspect = signal.CurrentAspect.ToString()
                });
            }

            // 4. EXTRACT AREA LABELS -- confirmed (via decompiling
            // Assembly-CSharp) that the base game's own minimap doesn't
            // label Model.Ops.Area at all -- it renders a literal top-down
            // camera view of the scene, and every visible name on it (yard
            // names, industry names, modded content alike) comes from a
            // plain UI.Map.MapLabel component (just a "text" string on a
            // world-positioned Canvas) via MapBuilder's own
            // FindObjectsOfType<MapLabel> call. Using the same component
            // here means modded areas that add their own MapLabel (the same
            // way they'd integrate with the base minimap) are picked up for
            // free, with no special-casing.
            //
            // Deliberately NOT includeInactive, same convention as the
            // CTCSignal loop above: Game.Progression.MapFeature -- the
            // milestone/unlock system -- disables a locked region's
            // gameObjectsEnableOnUnlock (which includes its MapLabel) via
            // GameObject.SetActive(false) until unlocked, so the default
            // active-only FindObjectsOfType query already excludes
            // not-yet-unlocked area names with no extra bookkeeping needed.
            foreach (var label in UnityEngine.Object.FindObjectsOfType<MapLabel>())
            {
                if (label == null || string.IsNullOrEmpty(label.text)) continue;

                // MapLabel.text is never set at runtime anywhere in the game's
                // own code (confirmed by decompiling) -- every value is
                // authored directly in the scene, including several that are
                // deliberately icon-only TextMeshPro sprite tags (e.g.
                // "<sprite name=\"Water\">", decorative only), not real place
                // names. Stripping rich-text tags and skipping anything left
                // blank filters those out. (Water service points are NOT
                // sourced from this label -- see RollingStock.CarLoadTargetLoader
                // below; this label turned out to be inconsistently authored
                // across water stations and an unreliable signal on its own.)
                string displayText = StripRichTextTags(label.text);
                if (string.IsNullOrWhiteSpace(displayText)) continue;

                Vector3 pos = WorldTransformer.WorldToGame(label.transform.position);
                network.areas.Add(new AreaDto
                {
                    id = !string.IsNullOrEmpty(label.name) ? label.name : displayText,
                    name = displayText,
                    position = new float[] { pos.x, MapCoordinates.MapZ(pos.z) }
                });
            }

            // 5. EXTRACT SERVICE POINTS (water/coal/diesel supply for
            // locomotives). NOT sourced from Industry/IndustryUnloader (an
            // earlier attempt at this): confirmed by decompiling the
            // WaypointQueue mod (which automates real Auto Engineer
            // refueling, so its detection logic is proven to actually work
            // in-game) that the real, authoritative source is
            // RollingStock.CarLoadTargetLoader -- a leaf MonoBehaviour
            // placed directly at the physical crane/chute/pump, entirely
            // independent of the Industry/waybill economy. Its own
            // `sourceIndustry` field is explicitly nullable ("if null,
            // unlimited loads are provided"), which is exactly why water
            // (unlimited, free) never appeared in the industry-based
            // "Fuel Inventory" report that coal/diesel are tracked through --
            // it was never an Industry-linked component to begin with, for
            // any of the three.
            //
            // WaypointQueue itself uses this component's raw
            // transform.position directly (no CenterPoint-style adjustment),
            // confirming it's already placed exactly at the real-world
            // supply point -- unlike IndustryUnloader/PassengerStop above,
            // this one genuinely needs no correction.
            foreach (var loader in UnityEngine.Object.FindObjectsOfType<CarLoadTargetLoader>())
            {
                if (loader == null) continue;

                // Best-effort, one loader at a time -- this whole method has
                // no outer try/catch (EnsureTrackCache doesn't wrap its call
                // either), so an uncaught exception anywhere in this loop
                // would abort extraction entirely and leave EVERYTHING
                // (segments/switches/signals/areas too, already built above)
                // undelivered for that tick, not just service points.
                try
                {
                    if (loader.load == null) continue;

                    // WaypointQueue matches on load.name.ToLower(), NOT
                    // load.id -- confirmed the two aren't interchangeable
                    // here: an earlier version of this code used .id (which
                    // works fine for IndustryUnloader elsewhere in this
                    // file) and it silently matched zero loaders. Mirror the
                    // proven-working mod's exact expression rather than
                    // assuming equivalence again.
                    string kind = loader.load.name?.ToLower() switch
                    {
                        "water" => "Water",
                        "coal" => "Coal",
                        "diesel-fuel" => "Diesel",
                        _ => null
                    };
                    if (kind == null) continue;

                    // GetInstanceID(), NOT loader.name -- confirmed (via a
                    // one-time diagnostic scan) every CarLoadTargetLoader in
                    // the game shares the identical GameObject name
                    // "Loader". Using that as the id meant the client's
                    // servicePoints Map (keyed by id) silently collapsed all
                    // 29+ real loaders down to whichever one happened to be
                    // processed last -- the actual cause of "icons not
                    // showing", not a matching or position bug at all.
                    Vector3 pos = WorldTransformer.WorldToGame(loader.transform.position);
                    network.servicePoints.Add(new ServicePointDto
                    {
                        id = $"{kind}_{loader.GetInstanceID()}",
                        kind = kind,
                        position = new float[] { pos.x, MapCoordinates.MapZ(pos.z) }
                    });
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not extract service point from CarLoadTargetLoader '{loader.name}': {ex.Message}");
                }
            }

            // 6. EXTRACT PASSENGER STOPS -- the base minimap needs no
            // dedicated icon for these either, for the same reason as
            // passenger station buildings generally: it's a literal camera
            // render of the actual platform geometry. We draw our own icon
            // since we don't have that 3D geometry to fall back on.
            // Active-only, same convention as signals/areas: PassengerStop
            // implements IProgressionDisablable, so ProgressionDisabled is
            // checked directly rather than relying on GameObject activation.
            //
            // One entry PER TRACK SPAN, not per station -- a station with
            // multiple platform tracks (PassengerStop.TrackSpans can have
            // more than one) previously collapsed onto a single point via
            // CenterPoint (which only ever looks at trackSpans[0]), so
            // stations that unload on more than one track were only ever
            // getting one icon, at one of their tracks arbitrarily. Each
            // span's real endpoints (TrackSpan.GetPoints(), already in
            // game-space -- see the CenterPoint fallback logic in
            // IndustryComponent for why no WorldToGame call is needed here)
            // and real Length are sent as-is, letting the client size/orient
            // the rectangle to the actual platform instead of us baking in
            // a fixed size or a rotation angle that a Flip X/Z toggle would
            // invert.
            foreach (var stop in UnityEngine.Object.FindObjectsOfType<PassengerStop>())
            {
                if (stop == null) continue;

                try
                {
                    if (stop.ProgressionDisabled) continue;

                    bool anySpan = false;
                    foreach (var span in stop.TrackSpans)
                    {
                        if (span == null) continue;

                        var points = span.GetPoints();
                        if (points == null || points.Count < 2) continue;

                        anySpan = true;
                        Vector3 a = points.First();
                        Vector3 b = points.Last();
                        network.passengerStops.Add(new PassengerStopDto
                        {
                            id = !string.IsNullOrEmpty(span.id) ? span.id : $"{stop.name}_{network.passengerStops.Count}",
                            name = stop.name,
                            positionA = new float[] { a.x, MapCoordinates.MapZ(a.z) },
                            positionB = new float[] { b.x, MapCoordinates.MapZ(b.z) },
                            length = span.Length
                        });
                    }

                    if (!anySpan)
                    {
                        // Rare fallback -- a stop with no usable track spans
                        // at all. CenterPoint here still needs no WorldToGame
                        // call for the same reason as the spans above.
                        Vector3 pos = stop.CenterPoint;
                        network.passengerStops.Add(new PassengerStopDto
                        {
                            id = stop.name,
                            name = stop.name,
                            positionA = new float[] { pos.x, MapCoordinates.MapZ(pos.z) },
                            positionB = new float[] { pos.x, MapCoordinates.MapZ(pos.z) },
                            length = 0f
                        });
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not extract passenger stop '{stop.name}': {ex.Message}");
                }
            }

            return new TrackNetworkExtractionResult
            {
                Network = network,
                SwitchNodes = switchNodes,
                Signals = signals
            };
        }

        private static string SegmentIdentifier(TrackSegment segment)
        {
            return !string.IsNullOrEmpty(segment.id) ? segment.id : segment.name;
        }

        private static string NodeIdentifier(TrackNode node)
        {
            return !string.IsNullOrEmpty(node.id) ? node.id : node.name;
        }

        private static readonly Regex RichTextTagPattern = new Regex("<[^>]+>", RegexOptions.Compiled);

        // Strips TextMeshPro rich-text tags (e.g. "<sprite name=...>",
        // "<color=...>...</color>") down to their plain visible text.
        private static string StripRichTextTags(string text)
        {
            return RichTextTagPattern.Replace(text, string.Empty).Trim();
        }
    }
}
