﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Timers;
using Utils.NET.IO;
using Utils.NET.Logging;
using Utils.NET.Net.Udp.Packets;
using Utils.NET.Net.Udp.Reliability;
using Utils.NET.Utils;

namespace Utils.NET.Net.Udp
{
    public abstract class UdpClient<TPacket> where TPacket : Packet
    {

        public enum ConnectStatus
        {
            Success,
            NoChallengeReceived,
            NoConnectedReceived,
            Disconnect
        }

        private enum ConnectionState
        {
            ReadyToConnect = 0,
            Connected = 1,
            AwaitingChallenge = 2,
            AwaitingConnected = 3,
            Disconnected = 4
        }

        /// <summary>
        /// The max size of a Udp packet
        /// </summary>
        private const int Max_Packet_Size = 512;

        /// <summary>
        /// The delay, is milliseconds, before resending a connection packet
        /// </summary>
        private const double Connection_Retry_Delay = 250;

#if DEBUG
        /// <summary>
        /// Percentage of simulated packet loss
        /// </summary>
        private const double Simulate_Packet_Loss_Percent = -1;
#endif

        /// <summary>
        /// The amount of times to resend a connection packet before failure
        /// </summary>
        private const int Connection_Retry_Amount = 10;

        /// <summary>
        /// Underlying system socket used to send and receive data
        /// </summary>
        private Socket socket;

        /// <summary>
        /// The EndPoint that this client is connecting or connected to
        /// </summary>
        private EndPoint remoteEndPoint;

        /// <summary>
        /// The state of the virtual connection
        /// </summary>
        private int state = 0;

        /// <summary>
        /// The current connection state of the client
        /// </summary>
        /// <value>The state.</value>
        private ConnectionState State => (ConnectionState)state;

        /// <summary>
        /// Factory used to create packets
        /// </summary>
        private PacketFactory<TPacket> packetFactory;

        /// <summary>
        /// Buffer used to hold received data
        /// </summary>
        private IO.Buffer buffer;

        /// <summary>
        /// Object used to sync sending states
        /// </summary>
        private object sendSync = new object();

        /// <summary>
        /// Bool determining if the client is currently sending
        /// </summary>
        private bool sending = false;

        /// <summary>
        /// Queue used to store data ready to send
        /// </summary>
        private Queue<UdpSendData> sendQueue = new Queue<UdpSendData>();

        /// <summary>
        /// Value used to syncronize disconnection calls
        /// </summary>
        private int disconnected = 0;

        /// <summary>
        /// The time that the last packet was sent
        /// </summary>
        private DateTime lastSent = DateTime.Now;

        /// <summary>
        /// The time that this client last received data
        /// </summary>
        private DateTime lastReceived = DateTime.Now;

        /// <summary>
        /// Salt generated locally
        /// </summary>
        private ulong localSalt;

        /// <summary>
        /// Salt generated by the server
        /// </summary>
        private ulong remoteSalt;

        /// <summary>
        /// Generated salt from server/client
        /// </summary>
        public ulong salt;

        /// <summary>
        /// Timer used to check timeouts and connection packet delivery
        /// </summary>
        private System.Timers.Timer timer;

        /// <summary>
        /// The amount of times a connection packet has been resent
        /// </summary>
        private int retryCount = 0;

        /// <summary>
        /// The local port that this client is bound to
        /// </summary>
        public int LocalPort => ((IPEndPoint)socket.LocalEndPoint).Port;

        /// <summary>
        /// The remote address this client sends to and received from
        /// </summary>
        public IPAddress RemoteAddress => ((IPEndPoint)remoteEndPoint).Address;

        /// <summary>
        /// Event called when this client disconnects
        /// </summary>
        public event Action<UdpClient<TPacket>> OnDisconnect;

        /// <summary>
        /// Packet channel subscriptions
        /// </summary>
        private PacketChannel<TPacket>[] channels;

        /// <summary>
        /// The default packet channel for every packet type
        /// </summary>
        private UnreliableChannel<TPacket> defaultChannel;

        #region Init

        public UdpClient()
        {
            Init();
        }

        private void Init()
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            packetFactory = new PacketFactory<TPacket>();
            buffer = new IO.Buffer(Max_Packet_Size);

            timer = new System.Timers.Timer(Connection_Retry_Delay / 2);
            timer.Elapsed += OnTimer;

            defaultChannel = new UnreliableChannel<TPacket>();
            ConfigurePacketChannel(defaultChannel);

            channels = new PacketChannel<TPacket>[packetFactory.TypeCount + 1];
            for (int i = 0; i < channels.Length; i++)
            {
                channels[i] = defaultChannel; // set every channel to the default
            }
        }

        public void SetConnectedTo(EndPoint endpoint, ulong salt, int localPort)
        {
            this.salt = salt;
            remoteEndPoint = endpoint;
            SetConnectionState(ConnectionState.Connected);
            socket.Bind(new IPEndPoint(IPAddress.Any, localPort));
            timer.Start();
            HandleConnected(ConnectStatus.Success);
        }

        /// <summary>
        /// Configures a packet channel with this client's values
        /// </summary>
        /// <param name="channel"></param>
        private void ConfigurePacketChannel(PacketChannel<TPacket> channel)
        {
            channel.SetFactory(packetFactory);
            channel.SetReceiveAction(HandlePacket);
            channel.SetSendAction(DoSendData);
            channel.SetWriteUdpHeader(WriteUdpHeader);
        }

        /// <summary>
        /// Creates an unreliable packet channel
        /// </summary>
        /// <returns></returns>
        public UnreliableChannel<TPacket> CreateUnreliableChannel()
        {
            var channel = new UnreliableChannel<TPacket>();
            ConfigurePacketChannel(channel);
            return channel;
        }

        /// <summary>
        /// Creates a reliable packet channel
        /// </summary>
        /// <returns></returns>
        public ReliableChannel<TPacket> CreateReliableChannel()
        {
            var channel = new ReliableChannel<TPacket>();
            ConfigurePacketChannel(channel);
            return channel;
        }

        /// <summary>
        /// Creates an ordered reliable packet channel
        /// </summary>
        /// <returns></returns>
        public OrderedReliableChannel<TPacket> CreateOrderedReliableChannel()
        {
            var channel = new OrderedReliableChannel<TPacket>();
            ConfigurePacketChannel(channel);
            return channel;
        }

        /// <summary>
        /// Sets a packet channel to a given id
        /// </summary>
        /// <param name="id"></param>
        /// <param name="channel"></param>
        public void SetPacketChannel(byte id, PacketChannel<TPacket> channel)
        {
            channels[id] = channel;
        }

        #endregion

        #region Timer

        private void OnTimer(object sender, EventArgs e)
        {
            var s = State;
            switch (s)
            {
                case ConnectionState.Connected:
                    return;
                    if ((DateTime.Now - lastReceived).TotalSeconds > 5)
                    {
                        Disconnect();
                    }
                    break;
                case ConnectionState.AwaitingChallenge:
                    if (CheckResend())
                    {
                        SendConnect();
                    }
                    else
                    {
                        ConnectFailed(ConnectionState.AwaitingChallenge, ConnectStatus.NoChallengeReceived);
                    }
                    break;
                case ConnectionState.AwaitingConnected:
                    if (CheckResend())
                    {
                        SendSolution();
                    }
                    else
                    {
                        ConnectFailed(ConnectionState.AwaitingConnected, ConnectStatus.NoConnectedReceived);
                    }
                    break;
            }
        }

        /// <summary>
        /// Checks the retry count to determine if a connection packet can be resent
        /// </summary>
        /// <returns></returns>
        private bool CheckResend()
        {
            int count = retryCount;
            if (Interlocked.CompareExchange(ref retryCount, count + 1, count) == count)
            {
                return retryCount < Connection_Retry_Amount;
            }
            return false;
        }

        #endregion

        #region Connection

        /// <summary>
        /// Connects to a given string ip address and port
        /// </summary>
        /// <param name="ip"></param>
        /// <param name="port"></param>
        public void Connect(string ip, int port)
        {
            Connect(new IPEndPoint(IPAddress.Parse(ip), port));
        }

        /// <summary>
        /// Connects to a given ip and port
        /// </summary>
        /// <param name="ip"></param>
        /// <param name="port"></param>
        public void Connect(long ip, int port)
        {
            Connect(new IPEndPoint(ip, port));
        }

        /// <summary>
        /// Connects to a given EndPoint
        /// </summary>
        /// <param name="endpoint"></param>
        public void Connect(EndPoint endpoint)
        {
            // return if already started connection process
            if (!SetConnectionState(ConnectionState.AwaitingChallenge, ConnectionState.ReadyToConnect)) return;
            remoteEndPoint = endpoint;
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            localSalt = Udp.GenerateLocalSalt();
            timer.Start();
            StartRead();
            SendConnect();
        }

        /// <summary>
        /// Sends the connection request packet
        /// </summary>
        private void SendConnect()
        {
            SendUdp(new UdpConnect(localSalt));
        }

        /// <summary>
        /// Sends the connection solution packet
        /// </summary>
        private void SendSolution()
        {
            SendUdp(new UdpSolution(salt));
        }

        /// <summary>
        /// Called when this client is connected to a remote host
        /// </summary>
        protected abstract void HandleConnected(ConnectStatus status);

        /// <summary>
        /// Sets the connection state from a previous state, and returns true if successful
        /// </summary>
        /// <returns><c>true</c>, if connection state was set, <c>false</c> otherwise.</returns>
        /// <param name="newState">New state.</param>
        /// <param name="fromState">From state.</param>
        private bool SetConnectionState(ConnectionState newState, ConnectionState fromState)
        {
            return Interlocked.CompareExchange(ref state, (int)newState, (int)fromState) == (int)fromState;
        }

        /// <summary>
        /// Sets the connection state atomically
        /// </summary>
        /// <param name="newState">New state.</param>
        private ConnectionState SetConnectionState(ConnectionState newState)
        {
            return (ConnectionState)Interlocked.Exchange(ref state, (int)newState);
        }

        /// <summary>
        /// Disconnect client from the virtual connection
        /// </summary>
        /// <returns>The disconnect.</returns>
        public bool Disconnect(bool initiate = true)
        {
            if (Interlocked.CompareExchange(ref disconnected, 1, 0) == 1) return false; // return if this method was already called
            var currentState = SetConnectionState(ConnectionState.Disconnected);
            switch (currentState)
            {
                case ConnectionState.Connected:
                    if (initiate)
                    {
                        SendUdp(new UdpDisconnect(salt, UdpDisconnectReason.ClientDisconnect)); // only send if already connected and initiating the disconnect
                    }
                    HandleDisconnect();
                    OnDisconnect?.Invoke(this);
                    socket.Close();
                    break;
                case ConnectionState.ReadyToConnect:
                    disconnected = 0;
                    SetConnectionState(ConnectionState.ReadyToConnect);
                    return true;
                case ConnectionState.AwaitingChallenge:
                case ConnectionState.AwaitingConnected:
                    ConnectFailed(ConnectionState.Disconnected, ConnectStatus.Disconnect);
                    return true;
            }

            timer.Stop();
            return true;
        }

        /// <summary>
        /// Connection failed if the current state equals the given state
        /// </summary>
        /// <param name="state"></param>
        /// <param name="status"></param>
        private void ConnectFailed(ConnectionState state, ConnectStatus status)
        {
            if (State != state) return;
            SetConnectionState(ConnectionState.ReadyToConnect);
            retryCount = 0;
            timer.Stop();
            HandleConnected(status);
        }

        /// <summary>
        /// Called once upon disconnect
        /// </summary>
        protected abstract void HandleDisconnect();

        #endregion

        #region Reading

        /// <summary>
        /// Starts reading for incoming packets
        /// </summary>
        public void StartRead()
        {
            BeginRead();
        }

        /// <summary>
        /// Starts reading for incoming packets
        /// </summary>
        private void BeginRead()
        {
            try
            {
                //var ip = (IPEndPoint)remoteEndPoint;
                //Log.Write("Receiving from: " + ip.Address + ":" + ip.Port + " At port: " + LocalPort);
                socket.BeginReceiveFrom(buffer.data, 0, Max_Packet_Size, SocketFlags.None, ref remoteEndPoint, OnRead, state);
            }
            catch (ObjectDisposedException disposedEx)
            {
                Disconnect();
                return;
            }
        }

        /// <summary>
        /// Read callback
        /// </summary>
        /// <param name="ar"></param>
        private void OnRead(IAsyncResult ar)
        {
            int length;
            try
            {
                length = socket.EndReceiveFrom(ar, ref remoteEndPoint);
            }
            catch (ObjectDisposedException disposedEx)
            {
                Disconnect();
                return;
            }

            // TODO handle failed read or socket closed

            try
            {
                ReceivedData(buffer.data, length);
            }
            finally
            {
                BeginRead();
            }
        }

        /// <summary>
        /// Packet data received
        /// </summary>
        /// <param name="length"></param>
        protected void ReceivedData(byte[] data, int length)
        {
            BitReader r = new BitReader(data, length);
            bool isUdp = r.ReadBool();
            byte id;
            if (isUdp)
            {
                id = r.ReadUInt8();
                var udpPacket = Udp.CreateUdpPacket(id);
                if (udpPacket == null) return;
                udpPacket.ReadPacket(r);
                HandleUdpPacket(udpPacket);
                return;
            }

            ulong receivedSalt = r.ReadUInt64();
            if (receivedSalt != salt) return; // salt mismatch, TODO disconnect

#if DEBUG
            if (Rand.Next(10000) / 100.0 < Simulate_Packet_Loss_Percent)
            {
                Log.Error("Stopped packet: " + r.ReadUInt16());
                return;
            }
#endif
            lastReceived = DateTime.Now;

            id = r.ReadUInt8(); // read packet type
            var channel = channels[id]; // get channel for packet type

            channel.ReceivePacket(r, id);
        }

        /// <summary>
        /// Handles a received packet
        /// </summary>
        /// <param name="packet">Packet.</param>
        protected abstract void HandlePacket(TPacket packet);

        /// <summary>
        /// Handles a received UdpPacket
        /// </summary>
        /// <param name="packet">Packet.</param>
        private void HandleUdpPacket(UdpPacket packet)
        {
            var currentState = State;
            switch (packet.Type)
            {
                case UdpPacketType.Challenge:
                    HandleChallenge((UdpChallenge)packet);
                    break;
                case UdpPacketType.Connected:
                    HandleConnected((UdpConnected)packet);
                    break;
                case UdpPacketType.Disconnect:
                    HandleDisconnect((UdpDisconnect)packet);
                    break;
            }
        }

        /// <summary>
        /// Responds to a challenge packet received from the remote server
        /// </summary>
        /// <param name="challenge">Challenge.</param>
        private void HandleChallenge(UdpChallenge challenge)
        {
            if (challenge.clientSalt != localSalt) return; // salt mismatch, could be spoofed sender
            if (!SetConnectionState(ConnectionState.AwaitingConnected, ConnectionState.AwaitingChallenge)) return;
            remoteSalt = challenge.serverSalt;
            salt = Udp.CreateSalt(localSalt, remoteSalt);
            retryCount = 0;
            SendSolution();
        }

        /// <summary>
        /// Handles a connected packet received from the remote host
        /// </summary>
        /// <param name="connected"></param>
        private void HandleConnected(UdpConnected connected)
        {
            if (connected.salt != salt) // salt mismatch, could be spoofed sender
            {
                Log.Write("UdpClient, invalid salt received in solution packet");
                return;
            }
            if (!SetConnectionState(ConnectionState.Connected, ConnectionState.AwaitingConnected)) return;
            remoteEndPoint = new IPEndPoint(((IPEndPoint)remoteEndPoint).Address, connected.port);
            retryCount = 0;
            HandleConnected(ConnectStatus.Success);
        }

        private void HandleDisconnect(UdpDisconnect disconnect)
        {
            Log.Error("Server disconnected client: " + disconnect.ReasonString);
            Disconnect(false);
        }

        #endregion

        #region Sending

        /// <summary>
        /// Sends a given packet
        /// </summary>
        /// <param name="packet">Packet.</param>
        public void Send(TPacket packet)
        {
            Send(new UdpSendData(packet, false));
        }

        /// <summary>
        /// Sends a given udp packet
        /// </summary>
        /// <param name="packet"></param>
        private void SendUdp(UdpPacket packet)
        {
            Send(new UdpSendData(packet, true));
        }

        /// <summary>
        /// Sends data or adds it to a queue if already sending
        /// </summary>
        /// <param name="data"></param>
        private void Send(UdpSendData data)
        {
            lock (sendSync)
            {
                if (sending)
                {
                    sendQueue.Enqueue(data);
                    return;
                }
                sending = true;
            }

            SendPacket(data);
        }

        /// <summary>
        /// Sends a packet to the remote
        /// </summary>
        /// <param name="packet">Packet.</param>
        private void SendPacket(UdpSendData data)
        {
            //Log.Write("Sending: " + data.packet.GetType().Name);
            lastSent = DateTime.Now;

            var w = new BitWriter();
            WriteUdpHeader(w, data);
            if (!data.udp)
            {
                var channel = channels[data.packet.Id];
                channel.SendPacket(w, (TPacket)data.packet);
            }
            else
            {
                data.packet.WritePacket(w);
                DoSendData(w.GetData());
            }
        }

        /// <summary>
        /// Writes the Udp packet header
        /// </summary>
        /// <param name="w"></param>
        /// <param name="data"></param>
        private void WriteUdpHeader(BitWriter w, UdpSendData data)
        {
            w.Write(data.udp);
            if (!data.udp)
            {
                w.Write(salt);
                w.Write(data.packet.Id);
            }
            else
            {
                w.Write(data.packet.Id);
            }
        }

        private void DoSendData(IO.Buffer package)
        {
            //var ip = (IPEndPoint)remoteEndPoint;
            //Log.Write("Sending to: " + ip.Address + ":" + ip.Port + " From port: " + LocalPort);
            socket.BeginSendTo(package.data, 0, package.size, SocketFlags.None, remoteEndPoint, OnSend, null);
        }

        /// <summary>
        /// Callback function for socket send calls
        /// </summary>
        /// <param name="ar">Ar.</param>
        private void OnSend(IAsyncResult ar)
        {
            int sent = socket.EndSendTo(ar);

            UdpSendData nextPacket;
            lock (sendSync)
            {
                if (sendQueue.Count == 0)
                {
                    sending = false;
                    return;
                }
                nextPacket = sendQueue.Dequeue();
            }

            SendPacket(nextPacket);
        }

        #endregion
    }
}
