using System;
using LiteNetLib.Utils;

namespace LiteNetLib
{
    enum PacketProperty : byte
    {
        None,                   //0
        Reliable,               //1
        Sequenced,              //2
        ReliableOrdered,        //3
        AckReliable,            //4
        AckReliableOrdered,     //5
        Ping,                   //6
        Pong,                   //7
        Connect,                //8
        Disconnect,             //9
        UnconnectedMessage,     //10
        NatIntroductionRequest, //11
        NatIntroduction,        //12
        NatPunchMessage,        //13
        MtuCheck,               //14
        MtuOk                   //15
    }

    sealed class NetPacket
    {
        const int LastProperty = 15;

        //Header
        public PacketProperty Property
        {
            get { return (PacketProperty)(RawData[0] & 0x7F); }
            set { RawData[0] = (byte)((RawData[0] & 0x80) | ((byte)value & 0x7F)); }
        }

        public ushort Sequence
        {
            get { return BitConverter.ToUInt16(RawData, 1); }
            set { FastBitConverter.GetBytes(RawData, 1, value); }
        }

        public bool Fragmented
        {
            get { return (RawData[0] & 0x80) != 0; }
            set
            {
                if (value)
                    RawData[0] |= 0x80; //set first bit
                else
                    RawData[0] &= 0x7F; //unset first bit
            }
        }

        public ushort FragmentId
        {
            get { return BitConverter.ToUInt16(RawData, 3); }
            set { FastBitConverter.GetBytes(RawData, 3, value); }
        }

        public uint FragmentPart
        {
            get { return BitConverter.ToUInt32(RawData, 5); }
            set { FastBitConverter.GetBytes(RawData, 5, value); }
        }

        public uint FragmentsTotal
        {
            get { return BitConverter.ToUInt32(RawData, 9); }
            set { FastBitConverter.GetBytes(RawData, 9, value); }
        }

        //Data
        public byte[] RawData;

        //Packet constructor
        public void Init(PacketProperty property, int dataSize)
        {
            RawData = new byte[GetHeaderSize(property) + dataSize];
            Property = property;
        }

        public void Init(PacketProperty property, NetDataWriter dataWriter)
        {
            RawData = new byte[GetHeaderSize(property) + dataWriter.Length];
            Property = property;
            Buffer.BlockCopy(dataWriter.Data, 0, RawData, GetHeaderSize(Property), dataWriter.Length);
        }

        public void PutData(byte[] data, int length)
        {
            Buffer.BlockCopy(data, 0, RawData, GetHeaderSize(Property), length);
        }

        public void PutData(byte[] data, int start, int length)
        {
            Buffer.BlockCopy(data, start, RawData, GetHeaderSize(Property), length);
        }

        public static bool GetPacketProperty(byte[] data, out PacketProperty property)
        {
            byte properyByte = data[0];
            if (properyByte > LastProperty)
            {
                property = PacketProperty.None;
                return false;
            }
            property = (PacketProperty)properyByte;
            return true;
        }

        public static bool ComparePacketProperty(byte[] data, PacketProperty check)
        {
            PacketProperty property;
            if (GetPacketProperty(data, out property))
            {
                return property == check;
            }
            return false;
        }

        public static int GetHeaderSize(PacketProperty property)
        {
            return IsSequenced(property)
                ? NetConstants.SequencedHeaderSize
                : NetConstants.HeaderSize;
        }

        public int GetHeaderSize()
        {
            return GetHeaderSize(Property);
        }

        public byte[] GetPacketData()
        {
            int headerSize = GetHeaderSize(Property);
            int dataSize = RawData.Length - headerSize;
            byte[] data = new byte[dataSize];
            Buffer.BlockCopy(RawData, headerSize, data, 0, dataSize);
            return data;
        }

        public bool IsClientData()
        {
            var property = Property;
            return property == PacketProperty.Reliable ||
                   property == PacketProperty.ReliableOrdered ||
                   property == PacketProperty.None ||
                   property == PacketProperty.Sequenced;
        }

        public static bool IsSequenced(PacketProperty property)
        {
            return property == PacketProperty.ReliableOrdered ||
                property == PacketProperty.Reliable ||
                property == PacketProperty.Sequenced ||
                property == PacketProperty.Ping ||
                property == PacketProperty.Pong ||
                property == PacketProperty.AckReliable ||
                property == PacketProperty.AckReliableOrdered;
        }

        public static byte[] GetUnconnectedData(byte[] raw, int count)
        {
            int size = count - NetConstants.HeaderSize;
            byte[] data = new byte[size];
            Buffer.BlockCopy(raw, 1, data, 0, size);
            return data;
        }

        //Packet contstructor from byte array
        public bool FromBytes(byte[] data, int packetSize)
        {
            //Reading property
            if (data[0] > LastProperty || packetSize > NetConstants.PacketSizeLimit)
                return false;
            RawData = new byte[packetSize];
            Buffer.BlockCopy(data, 0, RawData, 0, packetSize);
     
            return true;
        }
    }
}
