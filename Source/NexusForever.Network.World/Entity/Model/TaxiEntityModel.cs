using NexusForever.Network.Message;

namespace NexusForever.Network.World.Entity.Model
{
    public class TaxiEntityModel : IEntityModel
    {
        public class Passenger : IWritable
        {
            public byte SeatType { get; set; }
            public byte SeatPosition { get; set; }
            public uint UnitId { get; set; }

            public void Write(GamePacketWriter writer)
            {
                writer.Write(SeatType, 2);
                writer.Write(SeatPosition, 3);
                writer.Write(UnitId);
            }
        }

        public uint CreatureId { get; set; }
        public ushort UnitVehicleId { get; set; }
        public List<Passenger> Passengers { get; set; } = new();

        /// <remarks>
        /// A taxi has the vehicle layout, with no owner id: with one the client read the rest of the create misaligned and never made the cab.
        /// </remarks>
        public void Write(GamePacketWriter writer)
        {
            writer.Write(CreatureId, 18);
            writer.Write(UnitVehicleId, 14);

            writer.Write((byte)Passengers.Count, 3);
            Passengers.ForEach(s => s.Write(writer));
        }
    }
}
