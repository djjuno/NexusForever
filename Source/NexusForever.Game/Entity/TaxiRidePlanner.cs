using System.Numerics;
using NexusForever.GameTable;
using NexusForever.GameTable.Model;

namespace NexusForever.Game.Entity
{
    /// <summary>
    /// Finds the splines a taxi cab flies for the legs of a route.
    /// </summary>
    /// <remarks>
    /// TaxiRoute has no spline column and no other table names the spline of a route, but the splines are in Spline2 and Spline2Node: a spline on
    /// the route's world whose first node is next to the source node's WorldLocation2 and whose last node is next to the destination's.
    /// Almost every route has one, the legs between worlds and to housing have none and keep the teleport.
    /// Matching by distance is ours, how retail picked the spline of a route is not known.
    /// </remarks>
    public static class TaxiRidePlanner
    {
        /// <summary>
        /// Furthest a spline's end may be from the node it serves.
        /// </summary>
        public const float MaxEndDistance = 120f;

        public readonly record struct SplineEnds(uint SplineId, uint WorldId, Vector3 First, Vector3 Last, float Length);

        private static readonly object cacheLock = new();
        private static IGameTableManager cachedFor;
        private static Dictionary<uint, List<SplineEnds>> cachedByWorld;

        /// <summary>
        /// Return the spline of each leading leg of the route that has one on the supplied world, in order.
        /// </summary>
        /// <remarks>
        /// The plan stops at the first leg without a spline or on another world, the player is moved from there to the destination of the route.
        /// </remarks>
        public static List<SplineEnds> Plan(IGameTableManager gameTableManager, IReadOnlyList<TaxiRouteEntry> legs, uint worldId)
        {
            Dictionary<uint, List<SplineEnds>> byWorld = GetSplines(gameTableManager);

            var plan = new List<SplineEnds>();
            foreach (TaxiRouteEntry leg in legs)
            {
                if (!TryGetNode(gameTableManager, leg.TaxiNodeIdSource, out uint sourceWorld, out Vector3 source)
                    || !TryGetNode(gameTableManager, leg.TaxiNodeIdDestination, out uint destinationWorld, out Vector3 destination))
                    break;

                if (sourceWorld != worldId || destinationWorld != worldId)
                    break;

                if (!byWorld.TryGetValue(worldId, out List<SplineEnds> splines))
                    break;

                SplineEnds? spline = FindSpline(splines, source, destination);
                if (spline == null)
                    break;

                plan.Add(spline.Value);
            }

            return plan;
        }

        /// <summary>
        /// Return the spline whose first node is closest to the source and last node closest to the destination, both within <see cref="MaxEndDistance"/>.
        /// </summary>
        public static SplineEnds? FindSpline(IEnumerable<SplineEnds> splines, Vector3 source, Vector3 destination)
        {
            SplineEnds? best = null;
            float bestScore = float.MaxValue;
            foreach (SplineEnds spline in splines)
            {
                float fromSource    = Vector3.Distance(spline.First, source);
                float toDestination = Vector3.Distance(spline.Last, destination);
                if (fromSource > MaxEndDistance || toDestination > MaxEndDistance)
                    continue;

                float score = fromSource + toDestination;
                if (score < bestScore)
                {
                    bestScore = score;
                    best      = spline;
                }
            }

            return best;
        }

        /// <summary>
        /// Build the ends and length of each spline from the node rows, grouped by spline and ordered by ordinal.
        /// </summary>
        public static List<SplineEnds> BuildSplineEnds(IEnumerable<Spline2Entry> splines, IEnumerable<Spline2NodeEntry> nodes)
        {
            Dictionary<uint, uint> worlds = splines.ToDictionary(s => s.Id, s => s.WorldId);

            var result = new List<SplineEnds>();
            foreach (IGrouping<uint, Spline2NodeEntry> group in nodes.GroupBy(n => n.SplineId))
            {
                if (!worlds.TryGetValue(group.Key, out uint worldId))
                    continue;

                List<Vector3> points = group
                    .OrderBy(n => n.Ordinal)
                    .Select(n => new Vector3(n.Position0, n.Position1, n.Position2))
                    .ToList();
                if (points.Count < 2)
                    continue;

                float length = 0f;
                for (int i = 1; i < points.Count; i++)
                    length += Vector3.Distance(points[i - 1], points[i]);

                result.Add(new SplineEnds(group.Key, worldId, points[0], points[^1], length));
            }

            return result;
        }

        private static bool TryGetNode(IGameTableManager gameTableManager, uint taxiNodeId, out uint worldId, out Vector3 position)
        {
            worldId  = 0u;
            position = Vector3.Zero;

            TaxiNodeEntry node = gameTableManager.TaxiNode.GetEntry(taxiNodeId);
            if (node == null)
                return false;

            WorldLocation2Entry location = gameTableManager.WorldLocation2.GetEntry(node.WorldLocation2Id);
            if (location == null)
                return false;

            worldId  = location.WorldId;
            position = new Vector3(location.Position0, location.Position1, location.Position2);
            return true;
        }

        private static Dictionary<uint, List<SplineEnds>> GetSplines(IGameTableManager gameTableManager)
        {
            lock (cacheLock)
            {
                if (cachedFor != gameTableManager || cachedByWorld == null)
                {
                    cachedByWorld = BuildSplineEnds(gameTableManager.Spline2.Entries, gameTableManager.Spline2Node.Entries)
                        .GroupBy(s => s.WorldId)
                        .ToDictionary(g => g.Key, g => g.ToList());
                    cachedFor = gameTableManager;
                }

                return cachedByWorld;
            }
        }
    }
}
