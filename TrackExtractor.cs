using System;
using System.Collections.Generic;
using UnityEngine;
using RailroaderMinimapServer.Data;
using Helpers; // WorldTransformer.WorldToGame
using Track;
using Track.Signals; // CTCSignal, SignalAspect
using Model.Ops; // Area, OpsController

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

            // 4. EXTRACT AREAS -- yard/industry/interchange zones, the same
            // regions the base game's own minimap labels. Area is a real
            // scene MonoBehaviour (not just a lookup key), so it's enumerated
            // directly via OpsController.Shared.Areas rather than resolved
            // per-car the way destination coloring does.
            OpsController opsController = OpsController.Shared;
            if (opsController != null)
            {
                foreach (var area in opsController.Areas)
                {
                    if (area == null) continue;

                    Vector3 pos = WorldTransformer.WorldToGame(area.transform.position);
                    Color c = area.tagColor;
                    network.areas.Add(new AreaDto
                    {
                        id = !string.IsNullOrEmpty(area.identifier) ? area.identifier : area.name,
                        name = area.name,
                        position = new float[] { pos.x, MapCoordinates.MapZ(pos.z) },
                        radius = area.radius,
                        color = new int[]
                        {
                            Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f),
                            Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f),
                            Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f)
                        }
                    });
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
    }
}
