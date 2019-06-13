﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Utils.NET.IO;
using Utils.NET.Logging;
using Utils.NET.Net.Udp.Packets;

namespace Utils.NET.Net.Udp
{
    public abstract class UdpListener<TCon, TPacket> 
        where TPacket : Packet 
        where TCon : UdpClient<TPacket>
    {
        private class ConnectRequestState
        {
            /// <summary>
            /// Salt received by the client
            /// </summary>
            public ulong clientSalt;

            /// <summary>
            /// Salt generated by the server
            /// </summary>
            public ulong serverSalt;

            /// <summary>
            /// The ip address of the request
            /// </summary>
            public IPAddress address;

            public ConnectRequestState(ulong clientSalt, ulong serverSalt, IPAddress address)
            {
                this.clientSalt = clientSalt;
                this.serverSalt = serverSalt;
                this.address = address;
            }
        }

        private class SendData
        {
            /// <summary>
            /// The UdpPacket being sent
            /// </summary>
            public readonly UdpPacket packet;

            /// <summary>
            /// The EndPoint to send to
            /// </summary>
            public readonly EndPoint endpoint;

            public SendData(UdpPacket packet, EndPoint endpoint)
            {
                this.packet = packet;
                this.endpoint = endpoint;
            }
        }

        /// <summary>
        /// The port that the listener listens on
        /// </summary>
        private readonly int port;

        /// <summary>
        /// A queue consisting of all available ports
        /// </summary>
        private readonly ConcurrentQueue<int> availablePorts = new ConcurrentQueue<int>();

        /// <summary>
        /// Socket used to receive connection packets
        /// </summary>
        private Socket socket;

        /// <summary>
        /// The EndPoint used to receive packets
        /// </summary>
        private EndPoint receiveEndPoint;

        /// <summary>
        /// Buffer used to store received data
        /// </summary>
        private IO.Buffer buffer;

        /// <summary>
        /// Dictionary containing the states of pending connections
        /// </summary>
        private readonly ConcurrentDictionary<IPAddress, ConnectRequestState> requestStates = new ConcurrentDictionary<IPAddress, ConnectRequestState>();

        /// <summary>
        /// Dictionary containing all current connections
        /// </summary>
        private readonly ConcurrentDictionary<IPAddress, TCon> connections = new ConcurrentDictionary<IPAddress, TCon>();

        /// <summary>
        /// Queue of data ready to be sent
        /// </summary>
        private readonly Queue<SendData> sendQueue = new Queue<SendData>();

        /// <summary>
        /// True if the socket is current sending data
        /// </summary>
        private bool sending = false;

        #region Init

        public UdpListener(int port, int maxClients)
        {
            this.port = port;
            InitPorts(maxClients);
            InitSocket();
        }

        /// <summary>
        /// Fills the available ports queue will all ports
        /// </summary>
        /// <param name="maxClients"></param>
        private void InitPorts(int maxClients)
        {
            for (int i = 1; i <= maxClients; i++)
            {
                availablePorts.Enqueue(port + i);
            }
        }

        /// <summary>
        /// Initializes the socket for catching incoming connections
        /// </summary>
        private void InitSocket()
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));

            buffer = new IO.Buffer(512);
        }

        #endregion

        #region State Control

        /// <summary>
        /// Starts accepting new clients
        /// </summary>
        public void Start()
        {
            BeginRead();
        }

        /// <summary>
        /// Stops listening for new clients
        /// </summary>
        public void Stop()
        {

        }

        #endregion

        #region Packet Handlers

        /// <summary>
        /// Handles a received UdpPacket
        /// </summary>
        /// <param name="packet">Packet.</param>
        private void HandlePacket(UdpPacket packet, EndPoint endpoint)
        {
            switch (packet.Type)
            {
                case UdpPacketType.Connect:
                    HandleConnect((UdpConnect)packet, endpoint);
                    break;
                case UdpPacketType.Solution:
                    HandleSolution((UdpSolution)packet, endpoint);
                    break;
            }
        }

        /// <summary>
        /// Handles a connect packet
        /// </summary>
        /// <param name="connect">Connect.</param>
        private void HandleConnect(UdpConnect connect, EndPoint endpoint)
        {
            var ipEndpoint = (IPEndPoint)endpoint;
            var address = ipEndpoint.Address;

            if (availablePorts.Count == 0) // no available ports
            {
                Send(new UdpDisconnect(connect.clientSalt, UdpDisconnectReason.ServerFull), endpoint);
                return;
            }

            if (connections.TryGetValue(address, out var connection))
            {
                Send(new UdpDisconnect(connect.clientSalt, UdpDisconnectReason.ExistingConnection), endpoint);
                return;
            }

            var state = CreateConnectionRequest(connect.clientSalt, address);
            requestStates[address] = state;

            Send(new UdpChallenge(state.clientSalt, state.serverSalt), endpoint);
        }

        /// <summary>
        /// Handles a solution packet
        /// </summary>
        /// <param name="solution">Solution.</param>
        private void HandleSolution(UdpSolution solution, EndPoint endpoint)
        {
            var ipEndpoint = (IPEndPoint)endpoint;
            var address = ipEndpoint.Address;

            if (connections.TryGetValue(address, out var oldcon))
            {
                if (oldcon.salt != solution.salt)
                {
                    Log.Write("New connection failed: already an existing connection");
                    return;
                }

                Send(new UdpConnected(oldcon.salt, (ushort)oldcon.LocalPort), endpoint);
                return;
            }

            if (!requestStates.TryGetValue(address, out var state))
            {
                Log.Write("New connection failed: no request state found");
                return;
            }

            var saltSolution = Udp.CreateSalt(state.clientSalt, state.serverSalt);
            if (solution.salt != saltSolution)
            {
                Log.Write("New connection failed: salt solution invalid");
                return;
            }

            if (!requestStates.TryRemove(address, out state))
            {
                Log.Write("New connection failed: no request state to remove");
                return;
            }

            if (!availablePorts.TryDequeue(out int port))
            {
                Log.Write("New connection failed: failed to assign port");
                return;
            }

            var connection = (TCon)Activator.CreateInstance(typeof(TCon));

            if (!connections.TryAdd(address, connection))
            {
                connection.Disconnect(true);
                Log.Write("New connection failed: failed to add connection");
                return;
            }

            connection.SetConnectedTo(endpoint, saltSolution, port);
            HandleConnection(connection);
            connection.StartRead();

            Send(new UdpConnected(saltSolution, (ushort)port), endpoint);
        }

        /// <summary>
        /// Handles a new connection
        /// </summary>
        /// <param name="connection"></param>
        protected abstract void HandleConnection(TCon connection);

        #endregion

        #region Connecion Methods

        private ConnectRequestState CreateConnectionRequest(ulong clientSalt, IPAddress address)
        {
            ulong serverSalt = Udp.GenerateLocalSalt();
            return new ConnectRequestState(clientSalt, serverSalt, address);
        }

        #endregion

        #region Reading

        /// <summary>
        /// Starts receiving data
        /// </summary>
        private void BeginRead()
        {
            receiveEndPoint = new IPEndPoint(IPAddress.Any, 0);
            socket.BeginReceiveFrom(buffer.data, 0, buffer.maxSize, SocketFlags.None, ref receiveEndPoint, OnRead, null);
        }

        /// <summary>
        /// Read callback for the receiving socket
        /// </summary>
        /// <param name="ar">Ar.</param>
        private void OnRead(IAsyncResult ar)
        {
            EndPoint fromEndpoint = new IPEndPoint(IPAddress.Any, 0);
            int length = socket.EndReceiveFrom(ar, ref fromEndpoint);

            byte[] data = new byte[length];
            System.Buffer.BlockCopy(buffer.data, 0, data, 0, length);
            BeginRead(); // start reading for more packets

            BitReader r = new BitReader(data, length);
            bool isUdp = r.ReadBool();
            if (!isUdp) return; // only accept udp packets
            byte id = r.ReadUInt8();
            var packet = Udp.CreateUdpPacket(id);
            if (packet == null) return; // failed to create a packet from the given id value
            packet.ReadPacket(r);
            HandlePacket(packet, fromEndpoint);
        }

        #endregion

        #region Sending

        /// <summary>
        /// Sends a packet to a given endpoint
        /// </summary>
        /// <param name="packet">Packet.</param>
        /// <param name="endpoint">Endpoint.</param>
        private void Send(UdpPacket packet, EndPoint endpoint)
        {
            var data = new SendData(packet, endpoint);
            lock (sendQueue)
            {
                if (sending)
                {
                    sendQueue.Enqueue(data);
                    return;
                }
                sending = true;
            }

            Send(data);
        }

        /// <summary>
        /// Sends the data
        /// </summary>
        /// <param name="data">Data.</param>
        private void Send(SendData data)
        {
            var sendBuffer = PackagePacket(data);
            socket.BeginSendTo(sendBuffer.data, 0, sendBuffer.size, SocketFlags.None, data.endpoint, OnSend, null);
        }

        /// <summary>
        /// Send callback
        /// </summary>
        /// <param name="ar">Ar.</param>
        private void OnSend(IAsyncResult ar)
        {
            int sentLength = socket.EndSendTo(ar);

            SendData nextPacket;
            lock (sendQueue)
            {
                if (sendQueue.Count == 0)
                {
                    sending = false;
                    return;
                }
                nextPacket = sendQueue.Dequeue();
            }

            Send(nextPacket);
        }

        /// <summary>
        /// Packages a packet into a buffer to send
        /// </summary>
        /// <returns>The packet.</returns>
        /// <param name="data">The packet data to send</param>
        private IO.Buffer PackagePacket(SendData data)
        {
            var w = new BitWriter();
            w.Write(true);
            data.packet.WritePacket(w);
            return w.GetData();
        }

        #endregion
    }
}
