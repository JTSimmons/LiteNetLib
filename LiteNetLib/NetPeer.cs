using System;
using System.Collections.Generic;
using System.Diagnostics;
using LiteNetLib.Utils;

namespace LiteNetLib
{
    public sealed class NetPeer
    {
        //Flow control
        private int _currentFlowMode;
        private int _sendedPacketsCount;                    
        private int _flowTimer;

        //Ping and RTT
        private int _rtt;
        private int _avgRtt;
        private int _rttCount;
        private int _goodRttCount;
        private ushort _pingSequence;
        private ushort _remotePingSequence;

        private int _pingSendDelay;
        private int _pingSendTimer;
        private const int RttResetDelay = 1000;
        private int _rttResetTimer;

        private readonly Stopwatch _pingStopwatch;
        private readonly Stopwatch _lastPacketStopwatch;

        //Common
        private readonly NetSocket _socket;              
        private readonly Stack<NetPacket> _packetPool;
        private readonly NetEndPoint _remoteEndPoint;
        private readonly long _id;
        private readonly NetBase _peerListener;

        //Channels
        private readonly ReliableChannel _reliableOrderedChannel;
        private readonly ReliableChannel _reliableUnorderedChannel;
        private readonly SequencedChannel _sequencedChannel;
        private readonly SimpleChannel _simpleChannel;

        private int _windowSize = NetConstants.DefaultWindowSize;

        //MTU
        private int _mtu = NetConstants.PossibleMtu[0];
        private int _mtuIdx;
        private bool _finishMtu;
        private int _mtuCheckTimer;
        private int _mtuCheckAttempts;
        private const int MtuCheckDelay = 1000;
        private const int MaxMtuCheckAttempts = 10;
        private readonly object _mtuMutex = new object();

        //Fragment
        private class IncomingFragments
        {
            public NetPacket[] Fragments;
            public int ReceivedCount;
            public int TotalSize;
        }
        private ushort _fragmentId;
        private readonly Dictionary<ushort, IncomingFragments> _holdedFragments;

        private void ProcessFragmentedPacket(NetPacket p)
        {
            //Get needed array from dictionary
            ushort packetFragId = p.FragmentId;
            IncomingFragments incomingFragments;
            if (!_holdedFragments.TryGetValue(packetFragId, out incomingFragments))
            {
                incomingFragments = new IncomingFragments
                {
                    Fragments = new NetPacket[p.FragmentsTotal]
                };
            }

            //Cache
            var fragments = incomingFragments.Fragments;

            //Fill array
            fragments[p.FragmentPart] = p;

            //Increase data count
            incomingFragments.ReceivedCount++;
            int dataOffset = p.GetHeaderSize() - NetConstants.FragmentHeaderSize;
            incomingFragments.TotalSize += p.RawData.Length - dataOffset;

            //Check for finish
            if (incomingFragments.ReceivedCount != fragments.Length)
                return;

            NetPacket resultingPacket = GetPacketFromPool(p.Property, incomingFragments.TotalSize);
            for (int i = 0; i < incomingFragments.ReceivedCount; i++)
            {
                resultingPacket.PutData(fragments[i].RawData, dataOffset, p.RawData.Length - dataOffset);
                Recycle(fragments[i]);
                fragments[i] = null;
            }

            _peerListener.ReceiveFromPeer(resultingPacket, _remoteEndPoint);
            Recycle(resultingPacket);
            _holdedFragments.Remove(p.FragmentId);
        }

        //DEBUG
        internal ConsoleColor DebugTextColor = ConsoleColor.DarkGreen;

        public NetEndPoint EndPoint
        {
            get { return _remoteEndPoint; }
        }

        public int Ping
        {
            get { return _rtt; }
        }

        public int PingSendDelay
        {
            get { return _pingSendDelay; }
            set { _pingSendDelay = value; }
        }

        public int CurrentFlowMode
        {
            get { return _currentFlowMode; }
        }

        public long Id
        {
            get { return _id; }
        }

        public int Mtu
        {
            get { return _mtu; }
        }

        public long TimeSinceLastPacket
        {
            get { return _lastPacketStopwatch.ElapsedMilliseconds; }
        }

        internal NetPeer(NetBase peerListener, NetSocket socket, NetEndPoint remoteEndPoint)
        {
            _id = remoteEndPoint.GetId();
            _peerListener = peerListener;
            
            _socket = socket;
            _remoteEndPoint = remoteEndPoint;

            _avgRtt = 0;
            _rtt = 0;
            _pingSendDelay = NetConstants.DefaultPingSendDelay;
            _pingSendTimer = 0;

            _pingStopwatch = new Stopwatch();
            _lastPacketStopwatch = new Stopwatch();
            _lastPacketStopwatch.Start();

            _reliableOrderedChannel = new ReliableChannel(this, true, _windowSize);
            _reliableUnorderedChannel = new ReliableChannel(this, false, _windowSize);
            _sequencedChannel = new SequencedChannel(this);
            _simpleChannel = new SimpleChannel(this);

            _packetPool = new Stack<NetPacket>();
            _holdedFragments = new Dictionary<ushort, IncomingFragments>();
        }

        public void Send(byte[] data, SendOptions options)
        {
            Send(data, 0, data.Length, options);
        }

        public void Send(NetDataWriter dataWriter, SendOptions options)
        {
            Send(dataWriter.Data, 0, dataWriter.Length, options);
        }

        public void Send(byte[] data, int start, int length, SendOptions options)
        {
            //if (length > _mtu)
            //{
            //    int packetsCount = NetUtils.GetDividedPacketsCount(length, _mtu);
            //    for (int i = 0; i < packetsCount; i++)
            //    {
            //        byte[] fragmentedData = new byte[];
            //        Send();
            //    }
            //    return;
            //}

            NetPacket packet;
            switch (options)
            {
                case SendOptions.ReliableUnordered:
                    packet = GetPacketFromPool(PacketProperty.Reliable, length);
                    break;
                case SendOptions.Sequenced:
                    packet = GetPacketFromPool(PacketProperty.Sequenced, length);
                    break;
                case SendOptions.ReliableOrdered:
                    packet = GetPacketFromPool(PacketProperty.ReliableOrdered, length);
                    break;
                default:
                    packet = GetPacketFromPool(PacketProperty.None, length);
                    break;
            }
            packet.PutData(data, start, length);
            SendPacket(packet);
        }

        internal void CreateAndSend(PacketProperty property)
        {
            NetPacket packet = GetPacketFromPool(property);
            SendPacket(packet);
        }

        internal void CreateAndSend(PacketProperty property, ushort sequence)
        {
            NetPacket packet = GetPacketFromPool(property);
            packet.Sequence = sequence;
            SendPacket(packet);
        }

        internal void SendConnect(long id)
        {
            var connectPacket = GetPacketFromPool(PacketProperty.Connect, 8);
            FastBitConverter.GetBytes(connectPacket.RawData, 1, id);
            SendRawData(connectPacket.RawData);
        }

        internal void SendDisconnect(long id)
        {
            var disconnectPacket = GetPacketFromPool(PacketProperty.Disconnect, 8);
            FastBitConverter.GetBytes(disconnectPacket.RawData, 1, id);
            SendRawData(disconnectPacket.RawData);
        }

        internal static long GetConnectId(NetPacket connectPacket)
        {
            return BitConverter.ToInt64(connectPacket.RawData, 1);
        }

        //from user thread, our thread, or recv?
        private void SendPacket(NetPacket packet)
        {
            switch (packet.Property)
            {
                case PacketProperty.Reliable:
                    DebugWrite("[RS]Packet reliable");
                    _reliableUnorderedChannel.AddToQueue(packet);
                    break;
                case PacketProperty.Sequenced:
                    DebugWrite("[RS]Packet sequenced");
                    _sequencedChannel.AddToQueue(packet);
                    break;
                case PacketProperty.ReliableOrdered:
                    DebugWrite("[RS]Packet reliable ordered");
                    _reliableOrderedChannel.AddToQueue(packet);
                    break;
                case PacketProperty.AckReliable:
                case PacketProperty.AckReliableOrdered:
                case PacketProperty.None:
                    DebugWrite("[RS]Packet simple");
                    _simpleChannel.AddToQueue(packet);
                    break;
                case PacketProperty.Ping:
                case PacketProperty.Pong:
                case PacketProperty.Connect:
                case PacketProperty.Disconnect:
                case PacketProperty.MtuCheck:
                case PacketProperty.MtuOk:
                    SendRawData(packet.RawData);
                    Recycle(packet);
                    break;
                default:
                    throw new Exception("Unknown packet property: " + packet.Property);
            }
        }

        private void UpdateRoundTripTime(int roundTripTime)
        {
            //Calc average round trip time
            _rtt += roundTripTime;
            _rttCount++;
            _avgRtt = _rtt/_rttCount;

            //flowmode 0 = fastest
            //flowmode max = lowest

            if (_avgRtt < _peerListener.GetStartRtt(_currentFlowMode - 1))
            {
                if (_currentFlowMode <= 0)
                {
                    //Already maxed
                    return;
                }

                _goodRttCount++;
                if (_goodRttCount > NetConstants.FlowIncreaseThreshold)
                {
                    _goodRttCount = 0;
                    _currentFlowMode--;

                    DebugWrite("[PA]Increased flow speed, RTT: {0}, PPS: {1}", _avgRtt, _peerListener.GetPacketsPerSecond(_currentFlowMode));
                }
            }
            else if(_avgRtt > _peerListener.GetStartRtt(_currentFlowMode))
            {
                _goodRttCount = 0;
                if (_currentFlowMode < _peerListener.GetMaxFlowMode())
                {
                    _currentFlowMode++;
                    DebugWrite("[PA]Decreased flow speed, RTT: {0}, PPS: {1}", _avgRtt, _peerListener.GetPacketsPerSecond(_currentFlowMode));
                }
            }
        }

        [Conditional("DEBUG_MESSAGES")]
        internal void DebugWrite(string str, params object[] args)
        {
            NetUtils.DebugWrite(DebugTextColor, str, args);
        }

        [Conditional("DEBUG_MESSAGES"), Conditional("DEBUG")]
        internal void DebugWriteForce(string str, params object[] args)
        {
            NetUtils.DebugWriteForce(DebugTextColor, str, args);
        }

        internal NetPacket GetPacketFromPool(PacketProperty property = PacketProperty.None, int size=0, bool init=true)
        {
            NetPacket packet = null;
            lock (_packetPool)
            {
                if (_packetPool.Count > 0)
                {
                    packet = _packetPool.Pop();
                }
            }
            if(packet == null)
            {
                packet = new NetPacket();
            }
            if(init)
                packet.Init(property, size);
            return packet;
        }

        internal void Recycle(NetPacket packet)
        {
            lock (_packetPool)
            {
                packet.RawData = null;
                _packetPool.Push(packet);
            }
        }

        internal void AddIncomingPacket(NetPacket packet)
        {
            if (packet.IsFragmented)
            {
                ProcessFragmentedPacket(packet);
            }
            else
            {
                _peerListener.ReceiveFromPeer(packet, _remoteEndPoint);
                Recycle(packet);
            }
        }

        private void ProcessMtuPacket(NetPacket packet)
        {
            //MTU auto increase
            if (packet.Property == PacketProperty.MtuCheck)
            {
                if (_mtuIdx < NetConstants.PossibleMtu.Length - 1 && packet.RawData.Length > _mtu)
                {
                    DebugWriteForce("MTU check. Increase to: " + packet.RawData.Length);
                    lock (_mtuMutex)
                    {
                        _mtuIdx++;
                    }
                    _mtu = NetConstants.PossibleMtu[_mtuIdx];
                    _mtuCheckAttempts = 0;
                }

                var p = GetPacketFromPool(PacketProperty.MtuOk);
                p.RawData[1] = (byte) _mtuIdx;
                SendPacket(p);
            }
            else //MtuOk
            {
                lock (_mtuMutex)
                {
                    _mtuIdx = packet.RawData[1];
                }
                _mtu = NetConstants.PossibleMtu[_mtuIdx];
                DebugWriteForce("MTU ok. Increase to: " + _mtu);
            }

            //maxed.
            if (_mtuIdx == NetConstants.PossibleMtu.Length - 1)
            {
                _finishMtu = true;
            }
        }

        //Process incoming packet
        internal void ProcessPacket(NetPacket packet)
        {
            _lastPacketStopwatch.Reset();
            _lastPacketStopwatch.Start();

            DebugWrite("[RR]PacketProperty: {0}", packet.Property);
            switch (packet.Property)
            {
                //If we get ping, send pong
                case PacketProperty.Ping:
                    if (NetUtils.RelativeSequenceNumber(packet.Sequence, _remotePingSequence) < 0)
                    {
                        Recycle(packet);
                        break;
                    }
                    DebugWrite("[PP]Ping receive, send pong");
                    _remotePingSequence = packet.Sequence;
                    Recycle(packet);

                    //send
                    CreateAndSend(PacketProperty.Pong, _remotePingSequence);
                    break;

                //If we get pong, calculate ping time and rtt
                case PacketProperty.Pong:
                    if (NetUtils.RelativeSequenceNumber(packet.Sequence, _pingSequence) < 0)
                    {
                        Recycle(packet);
                        break;
                    }
                    _pingSequence = packet.Sequence;
                    int rtt = (int) _pingStopwatch.ElapsedMilliseconds;
                    _pingStopwatch.Reset();
                    UpdateRoundTripTime(rtt);
                    DebugWrite("[PP]Ping: {0}", rtt);
                    Recycle(packet);
                    break;

                //Process ack
                case PacketProperty.AckReliable:
                    _reliableUnorderedChannel.ProcessAck(packet);
                    Recycle(packet);
                    break;

                case PacketProperty.AckReliableOrdered:
                    _reliableOrderedChannel.ProcessAck(packet);
                    Recycle(packet);
                    break;

                //Process in order packets
                case PacketProperty.Sequenced:
                    _sequencedChannel.ProcessPacket(packet);
                    break;

                case PacketProperty.Reliable:
                    _reliableUnorderedChannel.ProcessPacket(packet);
                    break;

                case PacketProperty.ReliableOrdered:
                    _reliableOrderedChannel.ProcessPacket(packet);
                    break;

                //Simple packet without acks
                case PacketProperty.None:
                    AddIncomingPacket(packet);
                    return;

                case PacketProperty.MtuCheck:
                case PacketProperty.MtuOk:
                    ProcessMtuPacket(packet);
                    break;

                default:
                    DebugWriteForce("Error! Unexpected packet type: " + packet.Property);
                    break;
            }
        }

        internal bool SendRawData(byte[] data)
        {
            int errorCode = 0;
            if (_socket.SendTo(data, _remoteEndPoint, ref errorCode) == -1)
            {
                lock (_peerListener)
                {
                    _peerListener.ProcessSendError(_remoteEndPoint, errorCode.ToString());
                }
                return false;
            }
            return true;
        }

        internal void Update(int deltaTime)
        {
            //Get current flow mode
            int maxSendPacketsCount = _peerListener.GetPacketsPerSecond(_currentFlowMode);
            int availableSendPacketsCount = maxSendPacketsCount - _sendedPacketsCount;
            int currentMaxSend = Math.Min(availableSendPacketsCount, (maxSendPacketsCount*deltaTime) / NetConstants.FlowUpdateTime);

            DebugWrite("[UPDATE]Delta: {0}ms, MaxSend: {1}", deltaTime, currentMaxSend);

            //Pending acks
            _reliableOrderedChannel.SendAcks();
            _reliableUnorderedChannel.SendAcks();

            //Pending send
            int currentSended = 0;
            while (currentSended < currentMaxSend)
            {
                //Get one of packets
                if (_reliableOrderedChannel.SendNextPacket() ||
                    _reliableUnorderedChannel.SendNextPacket() ||
                    _sequencedChannel.SendNextPacket() ||
                    _simpleChannel.SendNextPacket())
                {
                    currentSended++;
                }
                else
                {
                    //no outgoing packets
                    break;
                }
            }

            //Increase counter
            _sendedPacketsCount += currentSended;

            //ResetFlowTimer
            _flowTimer += deltaTime;
            if (_flowTimer >= NetConstants.FlowUpdateTime)
            {
                DebugWrite("[UPDATE]Reset flow timer, _sendedPackets - {0}", _sendedPacketsCount);
                _sendedPacketsCount = 0;
                _flowTimer = 0;
            }

            //Send ping
            _pingSendTimer += deltaTime;
            if (_pingSendTimer >= _pingSendDelay)
            {
                DebugWrite("[PP] Send ping...");

                //reset timer
                _pingSendTimer = 0;

                //send ping
                CreateAndSend(PacketProperty.Ping, _pingSequence);

                //reset timer
                _pingStopwatch.Reset();
                _pingStopwatch.Start();
            }

            //RTT - round trip time
            _rttResetTimer += deltaTime;
            if (_rttResetTimer >= RttResetDelay)
            {
                _rttResetTimer = 0;
                //Rtt update
                _rtt = _avgRtt;
                _rttCount = 1;
            }

            //MTU - Maximum transmission unit
            if (!_finishMtu)
            {
                _mtuCheckTimer += deltaTime;
                if (_mtuCheckTimer >= MtuCheckDelay)
                {
                    _mtuCheckTimer = 0;
                    _mtuCheckAttempts++;
                    if (_mtuCheckAttempts >= MaxMtuCheckAttempts)
                    {
                        _finishMtu = true;
                    }
                    else
                    {
                        lock (_mtuMutex)
                        {
                            //Send increased packet
                            if (_mtuIdx < NetConstants.PossibleMtu.Length - 1)
                            {
                                int newMtu = NetConstants.PossibleMtu[_mtuIdx + 1] - NetConstants.HeaderSize;
                                var p = GetPacketFromPool(PacketProperty.MtuCheck, newMtu);
                                SendPacket(p);
                            }
                        }
                    }
                }
            }
        }
    }
}
