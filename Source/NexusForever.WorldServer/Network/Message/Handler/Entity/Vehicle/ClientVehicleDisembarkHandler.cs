using NexusForever.Game.Abstract.Entity;
using NexusForever.Network.Message;
using NexusForever.Network.World.Message.Model;

namespace NexusForever.WorldServer.Network.Message.Handler.Entity.Vehicle
{
    public class ClientVehicleDisembarkHandler : IMessageHandler<IWorldSession, ClientVehicleDisembark>
    {
        public void HandleMessage(IWorldSession session, ClientVehicleDisembark _)
        {
            // If player is mounted and tries to summon a different mount, the client sends this packet twice.
            // Ignore Disembark request if no vehicle.
            if (session.Player.PlatformGuid == null)
                return;

            // there is no way off a taxi, the cab lands the player at the destination
            if (session.Player.GetVisible<ITaxiRideEntity>(session.Player.PlatformGuid.Value) != null)
                return;

            session.Player.Dismount();
        }
    }
}
