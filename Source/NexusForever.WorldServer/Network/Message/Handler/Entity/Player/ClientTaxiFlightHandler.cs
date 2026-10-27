using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.Extensions.Logging;
using NexusForever.Game;
using NexusForever.Game.Abstract.Entity;
using NexusForever.Game.Entity;
using NexusForever.Game.Map;
using NexusForever.Game.Static.Entity;
using NexusForever.Game.Static.Reputation;
using NexusForever.GameTable;
using NexusForever.GameTable.Model;
using NexusForever.Network.Message;
using NexusForever.Network.World.Message.Model;
using NexusForever.Network.World.Message.Static;

namespace NexusForever.WorldServer.Network.Message.Handler.Entity.Player
{
    /// <summary>
    /// Taxi kiosk flights and shuttles, the player pays for the route and is moved to the destination of its last leg.
    /// </summary>
    /// <remarks>
    /// The price is the price the taxi map shows: for each leg the content tier of its destination node squared times GameFormula 952 (132.75),
    /// truncated. TaxiRoute.price matches the map for only a few routes and is 0 for many, they flew for free.
    /// The player rides a holo cab (<see cref="TaxiRideEntity"/>) along the splines of the route's legs on the player's world, a route whose first
    /// leg has no spline there is a teleport to the destination.
    /// </remarks>
    public class ClientTaxiFlightHandler : IMessageHandler<IWorldSession, ClientTaxiFlight>
    {
        private const float KioskRange = 15f;
        private const int MaxRouteLength = 30;

        #region Dependency Injection

        private readonly ILogger<ClientTaxiFlightHandler> log;
        private readonly IGameTableManager gameTableManager;
        private readonly IEntityFactory entityFactory;

        public ClientTaxiFlightHandler(
            ILogger<ClientTaxiFlightHandler> log,
            IGameTableManager gameTableManager,
            IEntityFactory entityFactory)
        {
            this.log              = log;
            this.gameTableManager = gameTableManager;
            this.entityFactory    = entityFactory;
        }

        #endregion

        public void HandleMessage(IWorldSession session, ClientTaxiFlight taxiFlight)
        {
            IPlayer player = session.Player;
            if (taxiFlight.Route.Count == 0 || taxiFlight.Route.Count > MaxRouteLength)
                return;

            TaxiRouteEntry[] legs = GetLegs(taxiFlight.Route.ToArray());
            if (legs == null)
            {
                log.LogWarning($"Taxi request from {player.Name} refused, route {string.Join(",", taxiFlight.Route)} is not a chain of valid legs.");
                return;
            }

            if (!CanFly(player, legs))
                return;

            // the taxi map can't be used in combat, a client that sent the purchase anyway is told why
            if (player.InCombat)
            {
                player.SendGenericError(GenericError.PlayerCannotWhileInCombat);
                return;
            }

            uint destination = legs[^1].TaxiNodeIdDestination;
            TaxiNodeEntry destinationEntry = gameTableManager.TaxiNode.GetEntry(destination);
            WorldLocation2Entry location = gameTableManager.WorldLocation2.GetEntry(destinationEntry.WorldLocation2Id);
            if (location == null || gameTableManager.World.GetEntry(location.WorldId) == null)
            {
                log.LogWarning($"Taxi request from {player.Name} refused, node {destination} has no valid world location.");
                return;
            }

            if (!player.CanTeleport() || player.IsOnTaxiRide)
                return;

            float perTier = gameTableManager.GameFormula.GetEntry(FareFormulaId)?.Datafloat0 ?? DefaultFarePerTier;
            ulong price = legs.Aggregate(0ul, (sum, leg) => sum + LegFare(gameTableManager.TaxiNode.GetEntry(leg.TaxiNodeIdDestination), perTier));
            if (!player.CurrencyManager.CanAfford(CurrencyType.Credits, price))
            {
                // the map shows the price in red but still sends the purchase
                player.SendGenericError(GenericError.VendorNotEnoughCash);
                return;
            }

            player.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, price);

            // flying somewhere discovers it
            player.DiscoverTaxiNode(destination);

            Vector3 rotation = new Quaternion(location.Facing0, location.Facing1, location.Facing2, location.Facing3).ToEuler();
            var position     = new Vector3(location.Position0, location.Position1, location.Position2);

            if (TryStartRide(player, legs, location, position, rotation))
                return;

            player.Rotation = rotation;
            player.TeleportTo((ushort)location.WorldId, location.Position0, location.Position1, location.Position2);
        }

        /// <summary>
        /// Seat the player in a cab that flies the splines of the route's legs on the player's world, false if the first leg has none.
        /// </summary>
        /// <remarks>
        /// Legs after the last spline, on another world or without one, are covered by the move to the destination when the cab lands.
        /// </remarks>
        private bool TryStartRide(IPlayer player, TaxiRouteEntry[] legs, WorldLocation2Entry location, Vector3 position, Vector3 rotation)
        {
            List<TaxiRidePlanner.SplineEnds> plan = TaxiRidePlanner.Plan(gameTableManager, legs, player.Map.Entry.Id);
            if (plan.Count == 0 || plan.Any(s => s.SplineId > ushort.MaxValue))
                return false;

            // off a mount first, the cab seats the player
            if (player.PlatformGuid != null)
                player.Dismount();

            var mapPosition = new MapPosition
            {
                Position = plan[0].First
            };

            var cab = entityFactory.CreateEntity<ITaxiRideEntity>();
            if (!player.Map.CanEnter(cab, mapPosition))
                return false;

            cab.Initialise(player, plan.Select(s => (ushort)s.SplineId).ToList(), plan.Sum(s => s.Length),
                (ushort)location.WorldId, (ushort)location.WorldZoneId, position, rotation);
            player.Map.EnqueueAdd(cab, mapPosition);

            log.LogTrace($"Taxi for {player.Name}: riding {plan.Count} of {legs.Length} leg(s), splines {string.Join(",", plan.Select(s => s.SplineId))}.");
            return true;
        }

        /// <summary>
        /// Return the legs of the route, null if a leg or node is invalid or a leg doesn't start where the previous one ended.
        /// </summary>
        private TaxiRouteEntry[] GetLegs(uint[] routeIds)
        {
            var legs = new TaxiRouteEntry[routeIds.Length];
            for (int i = 0; i < routeIds.Length; i++)
            {
                TaxiRouteEntry leg = gameTableManager.TaxiRoute.GetEntry(routeIds[i]);
                if (leg == null
                    || gameTableManager.TaxiNode.GetEntry(leg.TaxiNodeIdSource) == null
                    || gameTableManager.TaxiNode.GetEntry(leg.TaxiNodeIdDestination) == null)
                    return null;

                if (i > 0 && legs[i - 1].TaxiNodeIdDestination != leg.TaxiNodeIdSource)
                    return null;

                legs[i] = leg;
            }

            return legs;
        }

        /// <summary>
        /// The player has to be at a kiosk of the first node and every node on the route has to be neutral or of their faction.
        /// </summary>
        private bool CanFly(IPlayer player, TaxiRouteEntry[] legs)
        {
            // TaxiNode.taxiNodeFactionEnum: 0 neutral, 1 Exile, 2 Dominion
            uint faction = player.Faction1 switch
            {
                Faction.Exile    => 1u,
                Faction.Dominion => 2u,
                _                => 0u
            };

            bool otherFaction = legs
                .SelectMany(leg => new[] { leg.TaxiNodeIdSource, leg.TaxiNodeIdDestination })
                .Select(id => gameTableManager.TaxiNode.GetEntry(id).TaxiNodeFactionEnum)
                .Any(f => f != 0u && faction != 0u && f != faction);
            if (otherFaction)
                return false;

            uint source = legs[0].TaxiNodeIdSource;
            if (!player.IsNearTaxiNode(source, KioskRange))
            {
                log.LogWarning($"Taxi request from {player.Name} refused, no kiosk of node {source} within range.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// GameFormula row of the fare per content tier, and the fare per tier if the row is missing.
        /// </summary>
        private const uint FareFormulaId = 952u;
        private const float DefaultFarePerTier = 132.75f;

        /// <summary>
        /// Return the fare of a leg to the supplied destination node: its content tier squared times the fare per tier, truncated.
        /// </summary>
        private static ulong LegFare(TaxiNodeEntry destination, float perTier)
        {
            if (destination == null || destination.ContentTier == 0u)
                return 0ul;

            float fare = destination.ContentTier * perTier * destination.ContentTier;
            return fare > 0f ? (ulong)fare : 0ul;
        }
    }
}
