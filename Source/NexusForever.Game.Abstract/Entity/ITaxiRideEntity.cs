using System.Numerics;

namespace NexusForever.Game.Abstract.Entity
{
    /// <summary>
    /// The cab of a taxi kiosk flight: carries one <see cref="IPlayer"/> along the splines of the route and lands them at the destination.
    /// </summary>
    public interface ITaxiRideEntity : IVehicleEntity
    {
        /// <summary>
        /// Guid of the <see cref="IPlayer"/> the cab was called for.
        /// </summary>
        uint PassengerGuid { get; }

        /// <summary>
        /// Initialise the ride for <see cref="IPlayer"/>: the cab flies the supplied splines in order, then lands the player at the destination.
        /// </summary>
        void Initialise(IPlayer player, IReadOnlyList<ushort> splines, float totalLength, ushort destinationWorldId, ushort destinationZoneId, Vector3 destination, Vector3 destinationRotation);
    }
}
