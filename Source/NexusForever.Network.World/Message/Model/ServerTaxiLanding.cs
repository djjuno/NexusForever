using NexusForever.Network.Message;

namespace NexusForever.Network.World.Message.Model
{
    /// <summary>
    /// The taxi is landing, no payload.
    /// </summary>
    /// <remarks>
    /// The client plays the cab's landing line (UnitVehicle.soundEventIdLanding) only while the player still sits in the cab, send it before the
    /// passenger is removed. The takeoff line needs no message, the client plays it when the player is seated.
    /// </remarks>
    [Message(GameMessageOpcode.ServerTaxiLanding)]
    public class ServerTaxiLanding : IWritable
    {
        public void Write(GamePacketWriter writer)
        {
        }
    }
}
