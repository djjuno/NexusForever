using System.Numerics;
using NexusForever.Game.Abstract.Entity;
using NexusForever.Game.Abstract.Entity.Movement;
using NexusForever.Game.Abstract.Map;
using NexusForever.Game.Static.Entity;
using NexusForever.Game.Static.Entity.Movement.Spline;
using NexusForever.Network.World.Entity;
using NexusForever.Network.World.Entity.Model;
using NexusForever.Network.World.Message.Model;
using NLog;

namespace NexusForever.Game.Entity
{
    /// <summary>
    /// The holo cab of a taxi kiosk flight.
    /// </summary>
    /// <remarks>
    /// Creature 37280 "WORLD - Taxi - Taxi Mount - Speeder" with UnitVehicle 335: a taxi vehicle with one passenger seat and no pilot seat.
    /// The client plays the vehicle's takeoff sound itself when the player is seated. Retail had no way off a taxi: the cab ignores a disembark
    /// and lands the player at the destination when its last spline ends.
    /// </remarks>
    public class TaxiRideEntity : VehicleEntity, ITaxiRideEntity
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        public override EntityType Type => EntityType.Taxi;

        /// <summary>
        /// Creature of the holo cab.
        /// </summary>
        public const uint CabCreatureId = 37280u;

        /// <summary>
        /// Flight speed in metres per second, the air run speed of the cab's model (Creature2ModelInfo 1056).
        /// </summary>
        /// <remarks>
        /// The splines carry no speed, so this is a guess: the median route (1.2 km) is a 40 second ride and the longest (3.8 km) about two minutes.
        /// </remarks>
        public const float Speed = 30f;

        /// <summary>
        /// A passenger who is not seated by then (left the map or logged out while the cab was added) ends the ride.
        /// </summary>
        private static readonly TimeSpan SeatTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The cab has not moved for this long: the spline it was flying has ended.
        /// </summary>
        private const double StoppedTime = 0.25d;

        public uint PassengerGuid { get; private set; }

        private IReadOnlyList<ushort> splines = [];
        private int leg = -1;
        private bool seated;
        private bool arrived;
        private double elapsed;
        private double rideLimit;
        private Vector3 lastSplinePosition;
        private double stoppedFor;

        private ushort destinationWorldId;
        private Vector3 destination;
        private Vector3 destinationRotation;

        #region Dependency Injection

        public TaxiRideEntity(IMovementManager movementManager)
            : base(movementManager)
        {
        }

        #endregion

        /// <summary>
        /// Initialise the ride for <see cref="IPlayer"/>: the cab flies the supplied splines in order, then lands the player at the destination.
        /// </summary>
        public void Initialise(IPlayer player, IReadOnlyList<ushort> splines, float totalLength, ushort destinationWorldId, ushort destinationZoneId, Vector3 destination, Vector3 destinationRotation)
        {
            Initialise(CabCreatureId, 0u, 0u);

            PassengerGuid            = player.Guid;
            this.splines             = splines;
            this.destinationWorldId  = destinationWorldId;
            this.destination         = destination;
            this.destinationRotation = destinationRotation;

            // twice the flight time and a minute: the ride ends even if a spline never does
            rideLimit = totalLength / Speed * 2d + 60d;

            Rotation = player.Rotation;

            // shown at once: without it the client faded the new cab in and drew the rider, seated in the same flush, before the cab
            CreateFlags |= EntityCreateFlag.IsHoveringUnit;

            // a logout or crash during the ride saves the destination, the player paid for it
            player.SetTaxiArrival(destinationWorldId, destinationZoneId, destination, destinationRotation);

            EnqueuePassengerAdd(player, VehicleSeatType.Passenger, 0);
        }

        protected override IEntityModel BuildEntityModel()
        {
            // Always created empty: the rider is seated after every create of the cab instead (Player.AddVisible). A cab created again with its
            // rider listed in the create showed no rider.
            return new TaxiEntityModel
            {
                CreatureId    = CreatureId,
                UnitVehicleId = (ushort)VehicleEntry.Id
            };
        }

        /// <summary>
        /// The cab and its rider stay in each other's vision until it lands, whatever the range: a cab destroyed and created again at the rider's
        /// client was a new unit without the rider on it.
        /// </summary>
        public override bool KeepsInVision(IGridEntity entity)
        {
            return !arrived && entity is IPlayer && entity.Guid == PassengerGuid;
        }

        public override void OnAddToMap(IBaseMap map, uint guid, Vector3 vector)
        {
            // adding the cab sent its create to the rider: seat the rider now, in the same flush, instead of on the next update
            base.OnAddToMap(map, guid, vector);
            SeatPendingPassengers();
        }

        /// <summary>
        /// The platform comes before the seat: a seat sent before the platform left the rider standing in the air next to the cab.
        /// </summary>
        protected override void OnBeforePassengerSeat(IPlayer player, IVehiclePassenger passenger)
        {
            // the server moves the rider for the whole ride, without control the client stops sending its own position
            player.SetControl(null);

            // The rider's position is relative to its platform, (0, 0, 0) on the cab is the cab's own position: the map marker and other players
            // follow the cab with no further position update.
            player.SetPlatform(this);
            player.MovementManager.BroadcastNetworkEntityCommands();
        }

        protected override void OnPassengerAdd(IPlayer player, VehicleSeatType seatType, byte seatPosition)
        {
            seated = true;
            log.Trace($"Taxi {Guid}: player {PassengerGuid} seated, flying {splines.Count} spline(s) to world {destinationWorldId} {destination}.");

            // the rider can't fight back or get off, whatever was attacking them loses them now and nothing attacks them until they land
            player.ThreatManager.ClearThreatList();

            LaunchLeg(0);
        }

        private void LaunchLeg(int index)
        {
            leg                = index;
            stoppedFor         = 0d;
            lastSplinePosition = MovementManager.GetPosition();
            MovementManager.LaunchSpline(splines[index], SplineMode.OneShot, Speed, false);
        }

        public override void Update(double lastTick)
        {
            base.Update(lastTick);

            if (arrived || Map == null)
                return;

            elapsed += lastTick;

            if (!seated)
            {
                if (elapsed > SeatTimeout.TotalSeconds)
                {
                    log.Warn($"Taxi {Guid}: player {PassengerGuid} was never seated, ending the ride.");
                    arrived = true;
                    GetVisible<IPlayer>(PassengerGuid)?.ClearTaxiArrival();
                    RemoveFromMap();
                }

                return;
            }

            if (HasLegEnded(lastTick))
            {
                if (leg + 1 < splines.Count)
                    LaunchLeg(leg + 1);
                else
                    Land("arrived");
            }
            else if (elapsed > rideLimit)
                Land("took too long");
        }

        /// <summary>
        /// Returns if the spline of the current leg has ended.
        /// </summary>
        /// <remarks>
        /// The movement manager replaces a spline that has ended with a fixed position and only tells creature scripts: the leg is over once the cab
        /// stops moving.
        /// </remarks>
        private bool HasLegEnded(double lastTick)
        {
            Vector3 position = MovementManager.GetPosition();
            if (position != lastSplinePosition)
            {
                lastSplinePosition = position;
                stoppedFor         = 0d;
                return false;
            }

            stoppedFor += lastTick;
            return stoppedFor >= StoppedTime;
        }

        /// <summary>
        /// Take the passenger off and move them to the destination, the cab leaves the map with its last passenger.
        /// </summary>
        private void Land(string reason)
        {
            if (arrived)
                return;

            arrived = true;

            IPlayer player = GetVisible<IPlayer>(PassengerGuid);
            log.Trace($"Taxi {Guid}: player {PassengerGuid} landing ({reason}) after {elapsed:0} s, leg {leg + 1} of {splines.Count}.");

            if (player == null || GetPassenger(player.Guid) == null)
            {
                RemoveFromMap();
                return;
            }

            // the driver's goodbye, the client plays it only while the player still sits in the cab
            player.Session.EnqueueMessageEncrypted(new ServerTaxiLanding());

            PassengerRemove(player);
            MoveToDestination(player);
        }

        protected override void OnPassengerRemove(IPlayer player, VehicleSeatType seatType, byte seatPosition)
        {
            // the client moves its player again
            if (player.IsOnline && player.ControlGuid == null && player.Map != null)
                player.SetControl(player);

            if (arrived)
                return;

            // Off the cab some other way: a teleport (the teleport wins), a logout (the save keeps the destination) or death (the corpse is put at
            // the destination instead of in the air).
            arrived = true;
            log.Trace($"Taxi {Guid}: player {PassengerGuid} left the cab before landing.");

            if (!player.IsOnline)
                return;

            if (!player.IsAlive && player.CanTeleport())
            {
                MoveToDestination(player);
                return;
            }

            player.ClearTaxiArrival();
        }

        private void MoveToDestination(IPlayer player)
        {
            player.ClearTaxiArrival();
            if (!player.CanTeleport())
                return;

            player.Rotation = destinationRotation;
            player.TeleportTo(destinationWorldId, destination.X, destination.Y, destination.Z);
        }
    }
}
