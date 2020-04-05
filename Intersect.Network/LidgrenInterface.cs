﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

using Intersect.Logging;
using Intersect.Memory;
using Intersect.Network.Events;
using Intersect.Network.Packets;
using Intersect.Utilities;

using JetBrains.Annotations;

using Lidgren.Network;

namespace Intersect.Network
{

    public sealed class LidgrenInterface : INetworkLayerInterface
    {

        public delegate void HandleUnconnectedMessage(NetPeer peer, NetIncomingMessage message);

        private static readonly IConnection[] EmptyConnections = { };

        [NotNull] private readonly Ceras mCeras = new Ceras(true);

        [NotNull] private readonly IDictionary<long, Guid> mGuidLookup;

        [NotNull] private readonly INetwork mNetwork;

        [NotNull] private readonly NetPeer mPeer;

        [NotNull] private readonly NetPeerConfiguration mPeerConfiguration;

        [NotNull] private readonly RandomNumberGenerator mRng;

        [NotNull] private readonly RSACryptoServiceProvider mRsa;

        public LidgrenInterface(INetwork network, Type peerType, RSAParameters rsaParameters)
        {
            if (peerType == null)
            {
                throw new ArgumentNullException(nameof(peerType));
            }

            mNetwork = network ?? throw new ArgumentNullException(nameof(network));

            var configuration = mNetwork.Configuration;
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(mNetwork.Configuration));
            }

            mRng = new RNGCryptoServiceProvider();

            mRsa = new RSACryptoServiceProvider();
            mRsa.ImportParameters(rsaParameters);
            mPeerConfiguration = new NetPeerConfiguration(
                $"{VersionHelper.ExecutableVersion} {VersionHelper.LibraryVersion} {SharedConstants.VersionName}"
            )
            {
                AcceptIncomingConnections = configuration.IsServer
            };

            mPeerConfiguration.DisableMessageType(NetIncomingMessageType.Receipt);
            mPeerConfiguration.EnableMessageType(NetIncomingMessageType.UnconnectedData);

            if (configuration.IsServer)
            {
                mPeerConfiguration.EnableMessageType(NetIncomingMessageType.DiscoveryRequest);
                mPeerConfiguration.EnableMessageType(NetIncomingMessageType.ConnectionApproval);
                mPeerConfiguration.AcceptIncomingConnections = true;
                mPeerConfiguration.MaximumConnections = configuration.MaximumConnections;

                //mPeerConfiguration.LocalAddress = DnsUtils.Resolve(config.Host);
                //mPeerConfiguration.EnableUPnP = true;
                mPeerConfiguration.Port = configuration.Port;
            }

            if (Debugger.IsAttached)
            {
                mPeerConfiguration.EnableMessageType(NetIncomingMessageType.DebugMessage);
                mPeerConfiguration.EnableMessageType(NetIncomingMessageType.ErrorMessage);
                mPeerConfiguration.EnableMessageType(NetIncomingMessageType.Error);
            }
            else
            {
                mPeerConfiguration.DisableMessageType(NetIncomingMessageType.DebugMessage);
                mPeerConfiguration.DisableMessageType(NetIncomingMessageType.ErrorMessage);
                mPeerConfiguration.DisableMessageType(NetIncomingMessageType.Error);
            }

            if (Debugger.IsAttached)
            {
                mPeerConfiguration.ConnectionTimeout = 60;
                mPeerConfiguration.EnableMessageType(NetIncomingMessageType.VerboseDebugMessage);
            }
            else
            {
                mPeerConfiguration.ConnectionTimeout = 15;
                mPeerConfiguration.DisableMessageType(NetIncomingMessageType.VerboseDebugMessage);
            }

            mPeerConfiguration.PingInterval = 2.5f;
            mPeerConfiguration.UseMessageRecycling = true;

            var constructorInfo = peerType.GetConstructor(new[] {typeof(NetPeerConfiguration)});
            if (constructorInfo == null)
            {
                throw new ArgumentNullException(nameof(constructorInfo));
            }

            var constructedPeer = constructorInfo.Invoke(new object[] {mPeerConfiguration}) as NetPeer;
            mPeer = constructedPeer ?? throw new ArgumentNullException(nameof(constructedPeer));

            mGuidLookup = new Dictionary<long, Guid>();

            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            mPeer?.RegisterReceivedCallback(
                peer =>
                {
                    lock (mPeer)
                    {
                        if (OnPacketAvailable == null)
                        {
                            Log.Debug("Unhandled inbound Lidgren message.");
                            Log.Diagnostic($"Unhandled message: {TryHandleInboundMessage()}");

                            return;
                        }

                        OnPacketAvailable(this);
                    }
                }
            );
        }

        public HandleUnconnectedMessage OnUnconnectedMessage { get; set; }

        public HandleConnectionEvent OnConnectionApproved { get; set; }

        public HandleConnectionEvent OnConnectionDenied { get; set; }

        public HandleConnectionRequest OnConnectionRequested { get; set; }

        private bool IsDisposing { get; set; }

        public bool IsDisposed { get; private set; }

        public HandlePacketAvailable OnPacketAvailable { get; set; }

        public HandleConnectionEvent OnConnected { get; set; }

        public HandleConnectionEvent OnDisconnected { get; set; }

        public void Start()
        {
            if (mNetwork.Configuration.IsServer)
            {
                Log.Pretty.Info($"Listening on {mPeerConfiguration.LocalAddress}:{mPeerConfiguration.Port}.");
                mPeer.Start();

                return;
            }

            if (!Connect())
            {
                Log.Error("Failed to make the initial connection attempt.");
            }
        }

        public bool Connect()
        {
            if (mNetwork.Configuration.IsServer)
            {
                throw new InvalidOperationException("Server interfaces cannot use Connect().");
            }

            Log.Info($"Connecting to {mNetwork.Configuration.Host}:{mNetwork.Configuration.Port}...");

            var handshakeSecret = new byte[32];
            mRng.GetNonZeroBytes(handshakeSecret);

            var connectionRsa = new RSACryptoServiceProvider(2048);

            var hail = new HailPacket(
                mRsa, handshakeSecret, SharedConstants.VersionData, connectionRsa.ExportParameters(false)
            );

            var hailMessage = mPeer.CreateMessage();
            hailMessage.Data = hail.Data;
            hailMessage.LengthBytes = hailMessage.Data.Length;

            if (mPeer.Status == NetPeerStatus.NotRunning)
            {
                mPeer.Start();
            }

            var connection = mPeer.Connect(mNetwork.Configuration.Host, mNetwork.Configuration.Port, hailMessage);
            var server = new LidgrenConnection(
                mNetwork, Guid.Empty, connection, handshakeSecret, connectionRsa.ExportParameters(true)
            );

            if (mNetwork.AddConnection(server))
            {
                return true;
            }

            Log.Error("Failed to add connection to list.");
            connection?.Disconnect("client_error");

            return false;
        }

        public bool TryGetInboundBuffer(out IBuffer buffer, out IConnection connection)
        {
            buffer = default(IBuffer);
            connection = default(IConnection);

            var message = TryHandleInboundMessage();
            if (message == null)
            {
                return true;
            }

            var lidgrenId = message.SenderConnection?.RemoteUniqueIdentifier ?? -1;
            Debug.Assert(mGuidLookup != null, "mGuidLookup != null");
            if (!mGuidLookup.TryGetValue(lidgrenId, out var guid))
            {
                Log.Error($"Missing connection: {guid}");
                mPeer.Recycle(message);

                return false;
            }

            connection = mNetwork.FindConnection(guid);

            if (connection != null)
            {
                var lidgrenConnection = connection as LidgrenConnection;
                if (lidgrenConnection?.Aes == null)
                {
                    Log.Error("No provider to decrypt data with.");

                    return false;
                }

                if (!lidgrenConnection.Aes.Decrypt(message))
                {
                    Log.Error($"Error decrypting inbound Lidgren message [Connection:{connection.Guid}].");

                    return false;
                }
            }
            else
            {
                Log.Warn($"Received message from an unregistered endpoint.");
            }

            buffer = new LidgrenBuffer(message);

            return true;
        }

        public void ReleaseInboundBuffer(IBuffer buffer)
        {
            var message = (buffer as LidgrenBuffer)?.Buffer as NetIncomingMessage;
            mPeer?.Recycle(message);
        }

        public bool SendPacket(
            IPacket packet,
            IConnection connection = null,
            TransmissionMode transmissionMode = TransmissionMode.All
        )
        {
            if (connection == null)
            {
                return SendPacket(packet, EmptyConnections, transmissionMode);
            }

            var lidgrenConnection = connection as LidgrenConnection;
            if (lidgrenConnection == null)
            {
                Log.Diagnostic("Tried to send to a non-Lidgren connection.");

                return false;
            }

            var deliveryMethod = TranslateTransmissionMode(transmissionMode);
            if (mPeer == null)
            {
                throw new ArgumentNullException(nameof(mPeer));
            }

            if (packet == null)
            {
                Log.Diagnostic("Tried to send a null packet.");

                return false;
            }

            var message = mPeer.CreateMessage();
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            message.Data = packet.Data;
            message.LengthBytes = message.Data.Length;

            SendMessage(message, lidgrenConnection, NetDeliveryMethod.ReliableOrdered);

            return true;
        }

        public bool SendPacket(
            IPacket packet,
            ICollection<IConnection> connections,
            TransmissionMode transmissionMode = TransmissionMode.All
        )
        {
            var deliveryMethod = TranslateTransmissionMode(transmissionMode);
            if (mPeer == null)
            {
                throw new ArgumentNullException(nameof(mPeer));
            }

            if (packet == null)
            {
                Log.Diagnostic("Tried to send a null packet.");

                return false;
            }

            var message = mPeer.CreateMessage();
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            message.Data = packet.Data;
            message.LengthBytes = message.Data.Length;

            if (connections == null || connections.Count(connection => connection != null) < 1)
            {
                connections = mNetwork?.FindConnections<IConnection>();
            }

            var lidgrenConnections = connections?.OfType<LidgrenConnection>().ToList();
            if (lidgrenConnections?.Count > 0)
            {
                var firstConnection = lidgrenConnections.First();

                lidgrenConnections.ForEach(
                    lidgrenConnection =>
                    {
                        if (lidgrenConnection == null)
                        {
                            return;
                        }

                        if (firstConnection == lidgrenConnection)
                        {
                            return;
                        }

                        if (message.Data == null)
                        {
                            throw new ArgumentNullException(nameof(message.Data));
                        }

                        //var messageClone = mPeer.CreateMessage(message.Data.Length);
                        //if (messageClone == null)
                        //{
                        //    throw new ArgumentNullException(nameof(messageClone));
                        //}

                        //Buffer.BlockCopy(message.Data, 0, messageClone.Data, 0, message.Data.Length);
                        //messageClone.LengthBytes = message.LengthBytes;
                        SendMessage(message, lidgrenConnection, deliveryMethod);
                    }
                );

                SendMessage(message, lidgrenConnections.First(), deliveryMethod);
            }
            else
            {
                Log.Diagnostic("No lidgren connections, skipping...");
            }

            return true;
        }

        public void Stop(string reason = "stopping")
        {
            Disconnect(reason);
        }

        public void Disconnect(IConnection connection, string message)
        {
            Disconnect(new[] {connection}, message);
        }

        public void Disconnect(ICollection<IConnection> connections, string message)
        {
            if (connections == null)
            {
                return;
            }

            foreach (var connection in connections)
            {
                (connection as LidgrenConnection)?.NetConnection?.Disconnect(message);
                (connection as LidgrenConnection)?.NetConnection?.Peer.FlushSendQueue();
                (connection as LidgrenConnection)?.NetConnection?.Peer.Shutdown(message);
                (connection as LidgrenConnection)?.Dispose();
            }
        }

        public void Dispose()
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(LidgrenInterface));
            }

            if (IsDisposing)
            {
                return;
            }

            IsDisposing = true;

            switch (mPeer.Status)
            {
                case NetPeerStatus.NotRunning:
                case NetPeerStatus.ShutdownRequested:
                    break;

                case NetPeerStatus.Running:
                case NetPeerStatus.Starting:
                    mPeer.Shutdown(@"Terminating.");

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mPeer.Status));
            }

            IsDisposed = true;
            IsDisposing = false;
        }

        private NetIncomingMessage TryHandleInboundMessage()
        {
            Debug.Assert(mPeer != null, "mPeer != null");

            if (!mPeer.ReadMessage(out var message))
            {
                return null;
            }

            var connection = message.SenderConnection;
            var lidgrenId = connection?.RemoteUniqueIdentifier ?? -1;
            var lidgrenIdHex = BitConverter.ToString(BitConverter.GetBytes(lidgrenId));

            switch (message.MessageType)
            {
                case NetIncomingMessageType.Data:

                    //Log.Diagnostic($"{message.MessageType}: {message}");
                    return message;

                case NetIncomingMessageType.StatusChanged:
                    Debug.Assert(mGuidLookup != null, "mGuidLookup != null");
                    Debug.Assert(mNetwork != null, "mNetwork != null");

                    switch (connection?.Status ?? NetConnectionStatus.None)
                    {
                        case NetConnectionStatus.None:
                        case NetConnectionStatus.InitiatedConnect:
                        case NetConnectionStatus.ReceivedInitiation:
                        case NetConnectionStatus.RespondedAwaitingApproval:
                        case NetConnectionStatus.RespondedConnect:
                            Log.Diagnostic($"{message.MessageType}: {message} [{connection?.Status}]");

                            break;

                        case NetConnectionStatus.Disconnecting:
                            Log.Debug($"{message.MessageType}: {message} [{connection?.Status}]");

                            break;

                        case NetConnectionStatus.Connected:
                        {
                            LidgrenConnection intersectConnection;
                            if (!mNetwork.Configuration.IsServer)
                            {
                                intersectConnection = mNetwork.FindConnection<LidgrenConnection>(Guid.Empty);
                                if (intersectConnection == null)
                                {
                                    Log.Error("Bad state, no connection found.");
                                    mNetwork.Disconnect("client_connection_missing");
                                    connection?.Disconnect("client_connection_missing");

                                    break;
                                }

                                FireHandler(
                                    OnConnectionApproved,
                                    nameof(OnConnectionApproved),
                                    this,
                                    new ConnectionEventArgs
                                    {
                                        NetworkStatus = NetworkStatus.Connecting,
                                        Connection = intersectConnection
                                    }
                                );

                                Debug.Assert(connection != null, "connection != null");
                                var approval = (ApprovalPacket) mCeras.Deserialize(connection.RemoteHailMessage.Data);

                                if (!intersectConnection.HandleApproval(approval))
                                {
                                    mNetwork.Disconnect(NetworkStatus.HandshakeFailure.ToString());
                                    connection.Disconnect(NetworkStatus.HandshakeFailure.ToString());

                                    break;
                                }

                                if (!(mNetwork is ClientNetwork clientNetwork))
                                {
                                    throw new InvalidOperationException();
                                }

                                clientNetwork.AssignGuid(approval.Guid);

                                Debug.Assert(mGuidLookup != null, "mGuidLookup != null");
                                mGuidLookup.Add(connection.RemoteUniqueIdentifier, Guid.Empty);
                            }
                            else
                            {
                                Log.Diagnostic($"{message.MessageType}: {message} [{connection?.Status}]");
                                if (!mGuidLookup.TryGetValue(lidgrenId, out var guid))
                                {
                                    Log.Error($"Unknown client connected ({lidgrenIdHex}).");
                                    connection?.Disconnect("server_unknown_client");

                                    break;
                                }

                                intersectConnection = mNetwork.FindConnection<LidgrenConnection>(guid);
                            }

                            if (OnConnected != null)
                            {
                                intersectConnection?.HandleConnected();
                            }

                            FireHandler(
                                OnConnected,
                                nameof(OnConnected),
                                this,
                                new ConnectionEventArgs
                                {
                                    NetworkStatus = NetworkStatus.Online,
                                    Connection = intersectConnection
                                }
                            );
                        }

                            break;

                        case NetConnectionStatus.Disconnected:
                        {
                            Debug.Assert(connection != null, "connection != null");
                            Log.Debug($"{message.MessageType}: {message} [{connection.Status}]");
                            var result = (NetConnectionStatus) message.ReadByte();
                            var reason = message.ReadString();

                            NetworkStatus networkStatus;
                            try
                            {
                                switch (reason)
                                {
                                    //Lidgren won't accept a connection with a bad version and sends this message back so we need to manually handle it
                                    case "Wrong application identifier!":
                                        networkStatus = NetworkStatus.VersionMismatch;
                                        break;
                                    case "Connection timed out":
                                        networkStatus = NetworkStatus.Quitting;
                                        break;
                                    default:
                                        networkStatus = (NetworkStatus)Enum.Parse(typeof(NetworkStatus), reason, true);
                                        break;
                                }
                            }
                            catch (Exception exception)
                            {
                                Log.Diagnostic(exception);
                                networkStatus = NetworkStatus.Unknown;
                            }

                            HandleConnectionEvent disconnectHandler;
                            string disconnectHandlerName;
                            switch (networkStatus)
                            {
                                case NetworkStatus.Unknown:
                                case NetworkStatus.HandshakeFailure:
                                case NetworkStatus.ServerFull:
                                case NetworkStatus.VersionMismatch:
                                case NetworkStatus.Failed:
                                    disconnectHandler = OnConnectionDenied;
                                    disconnectHandlerName = nameof(OnConnectionDenied);
                                    break;

                                case NetworkStatus.Connecting:
                                case NetworkStatus.Online:
                                case NetworkStatus.Offline:
                                case NetworkStatus.Quitting:
                                    disconnectHandler = OnDisconnected;
                                    disconnectHandlerName = nameof(OnDisconnected);
                                    break;

                                default:
                                    throw new ArgumentOutOfRangeException();
                            }

                            if (!mGuidLookup.TryGetValue(lidgrenId, out var guid))
                            {
                                Log.Debug($"Unknown client disconnected ({lidgrenIdHex}).");
                                FireHandler(disconnectHandler, disconnectHandlerName, this, new ConnectionEventArgs { NetworkStatus = networkStatus });

                                break;
                            }

                            var client = mNetwork.FindConnection(guid);
                            if (client != null)
                            {
                                client.HandleDisconnected();

                                FireHandler(disconnectHandler, disconnectHandlerName, this, new ConnectionEventArgs { Connection = client, NetworkStatus = NetworkStatus.Offline });
                                mNetwork.RemoveConnection(client);
                            }

                            mGuidLookup.Remove(connection.RemoteUniqueIdentifier);
                        }

                            break;

                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    break;

                case NetIncomingMessageType.UnconnectedData:
                    OnUnconnectedMessage?.Invoke(mPeer, message);
                    Log.Diagnostic($"Net Incoming Message: {message.MessageType}: {message}");

                    break;

                case NetIncomingMessageType.ConnectionApproval:
                {
                    try
                    {
                        var hail = (HailPacket) mCeras.Deserialize(message.Data);

                        Debug.Assert(SharedConstants.VersionData != null, "SharedConstants.VERSION_DATA != null");
                        Debug.Assert(hail.VersionData != null, "hail.VersionData != null");
                        if (!SharedConstants.VersionData.SequenceEqual(hail.VersionData))
                        {
                            Log.Error($"Bad version detected, denying connection [{lidgrenIdHex}].");
                            connection?.Deny(NetworkStatus.VersionMismatch.ToString());

                            break;
                        }

                        if (OnConnectionApproved == null)
                        {
                            Log.Error($"No handlers for OnConnectionApproved, denying connection [{lidgrenIdHex}].");
                            connection?.Deny(NetworkStatus.Failed.ToString());

                            break;
                        }

                        /* Approving connection from here-on. */
                        var aesKey = new byte[32];
                        mRng?.GetNonZeroBytes(aesKey);
                        var client = new LidgrenConnection(mNetwork, connection, aesKey, hail.RsaParameters);

                        if (!OnConnectionRequested(this, client))
                        {
                            Log.Warn($"Connection blocked due to ban or ip filter!");
                            connection?.Deny(NetworkStatus.Failed.ToString());

                            break;
                        }

                        Debug.Assert(mNetwork != null, "mNetwork != null");
                        if (!mNetwork.AddConnection(client))
                        {
                            Log.Error($"Failed to add the connection.");
                            connection?.Deny(NetworkStatus.Failed.ToString());

                            break;
                        }

                        Debug.Assert(mGuidLookup != null, "mGuidLookup != null");
                        Debug.Assert(connection != null, "connection != null");
                        mGuidLookup.Add(connection.RemoteUniqueIdentifier, client.Guid);

                        Debug.Assert(mPeer != null, "mPeer != null");
                        var approval = new ApprovalPacket(client.Rsa, hail.HandshakeSecret, aesKey, client.Guid);
                        var approvalMessage = mPeer.CreateMessage();
                        approvalMessage.Data = approval.Data;
                        approvalMessage.LengthBytes = approvalMessage.Data.Length;
                        connection.Approve(approvalMessage);
                        OnConnectionApproved(this, new ConnectionEventArgs { Connection = client, NetworkStatus = NetworkStatus.Online });
                    }
                    catch
                    {
                        connection?.Deny(NetworkStatus.Failed.ToString());
                    }

                    break;
                }

                case NetIncomingMessageType.VerboseDebugMessage:
                    Log.Diagnostic($"Net Incoming Message: {message.MessageType}: {message.ReadString()}");

                    break;

                case NetIncomingMessageType.DebugMessage:
                    Log.Debug($"Net Incoming Message: {message.MessageType}: {message.ReadString()}");

                    break;

                case NetIncomingMessageType.WarningMessage:
                    Log.Warn($"Net Incoming Message: {message.MessageType}: {message.ReadString()}");

                    break;

                case NetIncomingMessageType.ErrorMessage:
                case NetIncomingMessageType.Error:
                    Log.Error($"Net Incoming Message: {message.MessageType}: {message.ReadString()}");

                    break;

                case NetIncomingMessageType.Receipt:
                    Log.Info($"Net Incoming Message: {message.MessageType}: {message.ReadString()}");

                    break;

                case NetIncomingMessageType.DiscoveryRequest:
                case NetIncomingMessageType.DiscoveryResponse:
                case NetIncomingMessageType.NatIntroductionSuccess:
                case NetIncomingMessageType.ConnectionLatencyUpdated:
                    Log.Diagnostic($"Net Incoming Message: {message.MessageType}: {message}");

                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            return null;
        }

        private bool FireHandler(
            HandleConnectionEvent handler,
            string name,
            [NotNull] INetworkLayerInterface sender,
            [NotNull] ConnectionEventArgs connectionEventArgs
        )
        {
            handler?.Invoke(sender, connectionEventArgs);

            if (handler == null)
            {
                Log.Error($"No handlers for '{name}'.");
            }

            return handler != null;
        }

        private void SendMessage(
            NetOutgoingMessage message,
            LidgrenConnection connection,
            NetDeliveryMethod deliveryMethod,
            int sequenceChannel = 0
        )
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            if (connection.NetConnection == null)
            {
                throw new ArgumentNullException(nameof(connection.NetConnection));
            }

            message.Encrypt(connection.Aes);
            connection.NetConnection.SendMessage(message, deliveryMethod, sequenceChannel);
        }

        private static NetDeliveryMethod TranslateTransmissionMode(TransmissionMode transmissionMode)
        {
            switch (transmissionMode)
            {
                case TransmissionMode.Any:
                    return NetDeliveryMethod.Unreliable;

                case TransmissionMode.Latest:
                    return NetDeliveryMethod.ReliableSequenced;

                // ReSharper disable once RedundantCaseLabel
                case TransmissionMode.All:
                default:
                    return NetDeliveryMethod.ReliableOrdered;
            }
        }

        internal bool Disconnect(string message)
        {
            mPeer.Connections?.ForEach(connection => connection?.Disconnect(message));

            return true;
        }

    }

}
