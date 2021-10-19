﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;

namespace RiptideNetworking
{
    /// <summary>Represents a server which can accept connections from clients.</summary>
    public class Server : RudpSocket
    {
        /// <summary>Whether or not the server is currently running.</summary>
        public bool IsRunning { get; private set; }
        /// <summary>The local port that the server is running on.</summary>
        public ushort Port { get; private set; }
        /// <summary>An array of all the currently connected clients.</summary>
        /// <remarks>The position of each <see cref="ServerClient"/> instance in the array does NOT correspond to that client's numeric ID.</remarks>
        public ServerClient[] Clients => clients.Values.ToArray();
        /// <summary>The maximum number of clients that can be connected at any time.</summary>
        public ushort MaxClientCount { get; private set; }
        /// <summary>The number of currently connected clients.</summary>
        public int ClientCount => clients.Count;
        /// <summary>The time (in milliseconds) after which to disconnect a client without a heartbeat.</summary>
        public ushort ClientTimeoutTime { get; set; } = 5000;
        private ushort _clientHeartbeatInterval;
        /// <summary>The interval (in milliseconds) at which heartbeats are to be expected from clients.</summary>
        public ushort ClientHeartbeatInterval
        {
            get => _clientHeartbeatInterval;
            set
            {
                _clientHeartbeatInterval = value;
                if (heartbeatTimer != null)
                    heartbeatTimer.Change(0, value);
            }
        }

        /// <summary>Currently connected clients, accessible by their endpoints.</summary>
        private Dictionary<IPEndPoint, ServerClient> clients;
        /// <summary>Endpoints of clients that have timed out and need to be removed from the <see cref="clients"/> dictionary.</summary>
        private List<IPEndPoint> timedOutClients;
        /// <summary>All currently unused client IDs.</summary>
        private List<ushort> availableClientIds;
        /// <summary>The timer responsible for sending regular heartbeats.</summary>
        private Timer heartbeatTimer;

        /// <summary>Encapsulates a method that handles a message from a certain client.</summary>
        /// <param name="fromClient">The client from whom the message was received.</param>
        /// <param name="message">The message that was received.</param>
        public delegate void MessageHandler(ServerClient fromClient, Message message);
        /// <summary>Methods used to handle messages, accessible by their corresponding message IDs.</summary>
        private Dictionary<ushort, MessageHandler> messageHandlers;
        /// <summary>The ID of the group of message handler methods to use when building <see cref="messageHandlers"/>.</summary>
        private byte messageHandlerGroupId;

        /// <summary>Handles initial setup.</summary>
        /// <param name="logName">The name to use when logging messages via <see cref="RiptideLogger"/>.</param>
        public Server(string logName = "SERVER") : base(logName) { }

        /// <summary>Starts the server.</summary>
        /// <param name="port">The local port on which to start the server.</param>
        /// <param name="maxClientCount">The maximum number of concurrent connections to allow.</param>
        /// <param name="clientHeartbeatInterval">The interval (in milliseconds) at which heartbeats are to be expected from clients.</param>
        /// <param name="messageHandlerGroupId">The ID of the group of message handler methods to use when building <see cref="messageHandlers"/>.</param>
        public void Start(ushort port, ushort maxClientCount, ushort clientHeartbeatInterval = 1000, byte messageHandlerGroupId = 0)
        {
            Port = port;
            MaxClientCount = maxClientCount;
            clients = new Dictionary<IPEndPoint, ServerClient>(MaxClientCount);
            timedOutClients = new List<IPEndPoint>(MaxClientCount);
            
            this.messageHandlerGroupId = messageHandlerGroupId;
            CreateMessageHandlersDictionary(Assembly.GetCallingAssembly(), messageHandlerGroupId);

            InitializeClientIds();
            _clientHeartbeatInterval = clientHeartbeatInterval;

            StartListening(port);

            heartbeatTimer = new Timer(Heartbeat, null, 0, ClientHeartbeatInterval);
            IsRunning = true;

            if (ShouldOutputInfoLogs)
                RiptideLogger.Log(LogName, $"Started on port {port}.");
        }

        /// <inheritdoc/>
        protected override void CreateMessageHandlersDictionary(Assembly assembly, byte messageHandlerGroupId)
        {
            MethodInfo[] methods = assembly.GetTypes()
                                           .SelectMany(t => t.GetMethods())
                                           .Where(m => m.GetCustomAttributes(typeof(MessageHandlerAttribute), false).Length > 0)
                                           .ToArray();

            messageHandlers = new Dictionary<ushort, MessageHandler>(methods.Length);
            for (int i = 0; i < methods.Length; i++)
            {
                MessageHandlerAttribute attribute = methods[i].GetCustomAttribute<MessageHandlerAttribute>();
                if (attribute.GroupId != messageHandlerGroupId)
                    break;

                ushort messageId = attribute.MessageId;
                if (messageHandlers.ContainsKey(messageId))
                    RiptideLogger.Log("ERROR", $"Message handler method already exists for message ID {messageId}! Only one handler method is allowed per ID!");
                else
                {
                    Delegate clientMessageHandler = Delegate.CreateDelegate(typeof(MessageHandler), methods[i], false);
                    if (clientMessageHandler != null)
                        messageHandlers.Add(messageId, (MessageHandler)clientMessageHandler);
                    else
                    {
                        // It's not a message handler for Server instances, but it might be one for Client instances
                        Delegate serverMessageHandler = Delegate.CreateDelegate(typeof(Client.MessageHandler), methods[i], false);
                        if (serverMessageHandler == null)
                            RiptideLogger.Log("ERROR", $"Method '{methods[i].Name}' didn't match a message handler signature!");
                    }
                }
            }
        }

        /// <summary>Checks if clients have timed out. Called by <see cref="heartbeatTimer"/>.</summary>
        private void Heartbeat(object state)
        {
            lock (clients)
            {
                foreach (ServerClient client in clients.Values)
                {
                    if (client.HasTimedOut)
                        timedOutClients.Add(client.remoteEndPoint);
                }
                
                foreach (IPEndPoint clientEndPoint in timedOutClients)
                    HandleDisconnect(clientEndPoint); // Disconnect the client

                timedOutClients.Clear();
            }
        }

        /// <inheritdoc/>
        protected override bool ShouldHandleMessageFrom(IPEndPoint endPoint, byte firstByte)
        {
            lock (clients)
            {
                if (clients.ContainsKey(endPoint))
                {
                    // Client is already connected
                    if ((HeaderType)firstByte != HeaderType.connect) // It's not a connect message, so handle it
                        return true;
                }
                else if (clients.Count < MaxClientCount)
                {
                    // Client is not yet connected and the server has capacity
                    if ((HeaderType)firstByte == HeaderType.connect) // It's a connect message, which doesn't need to be handled like other messages
                        clients.Add(endPoint, new ServerClient(this, endPoint, GetAvailableClientId()));
                }
                return false;
            }
        }

        /// <inheritdoc/>
        internal override void Handle(byte[] data, IPEndPoint fromEndPoint, HeaderType headerType)
        {
            Message message = Message.Create(headerType, data);
            
#if DETAILED_LOGGING
            if (headerType != HeaderType.reliable && headerType != HeaderType.unreliable)
                RiptideLogger.Log(LogName, $"Received {headerType} message from {fromEndPoint}."); 

            ushort messageId = message.PeekUShort();
            if (headerType == HeaderType.reliable)
                RiptideLogger.Log(LogName, $"Received reliable message (ID: {messageId}) from {fromEndPoint}.");
            else if (headerType == HeaderType.unreliable)
                RiptideLogger.Log(LogName, $"Received message (ID: {messageId}) from {fromEndPoint}.");
#endif

            switch (headerType)
            {
                // User messages
                case HeaderType.unreliable:
                case HeaderType.reliable:
                    receiveActionQueue.Add(() =>
                    {
                        if (clients.TryGetValue(fromEndPoint, out ServerClient client))
                        {
                            ushort messageId = message.GetUShort();
                            OnMessageReceived(new ServerMessageReceivedEventArgs(client, messageId, message));

                            if (messageHandlers.TryGetValue(messageId, out MessageHandler messageHandler))
                                messageHandler(client, message);
                            else
                                RiptideLogger.Log("ERROR", $"No handler method found for message ID {messageId}!");
                        }

                        message.Release();
                    });
                    return;

                // Internal messages
                case HeaderType.ack:
                    clients[fromEndPoint].HandleAck(message);
                    break;
                case HeaderType.ackExtra:
                    clients[fromEndPoint].HandleAckExtra(message);
                    break;
                case HeaderType.connect:
                    // Handled in ShouldHandleMessageFrom method
                    break;
                case HeaderType.heartbeat:
                    clients[fromEndPoint].HandleHeartbeat(message);
                    break;
                case HeaderType.welcome:
                    clients[fromEndPoint].HandleWelcomeReceived(message);
                    break;
                case HeaderType.clientConnected:
                case HeaderType.clientDisconnected:
                    break;
                case HeaderType.disconnect:
                    HandleDisconnect(fromEndPoint);
                    break;
                default:
                    RiptideLogger.Log("ERROR", $"Unknown message header type '{headerType}'! Discarding {data.Length} bytes.");
                    return;
            }

            message.Release();
        }

        /// <inheritdoc/>
        internal override void ReliableHandle(byte[] data, IPEndPoint fromEndPoint, HeaderType headerType)
        {
            ReliableHandle(data, fromEndPoint, headerType, clients[fromEndPoint].SendLockables);
        }

        /// <inheritdoc/>
        protected override void SendAck(ushort forSeqId, IPEndPoint toEndPoint)
        {
            clients[toEndPoint].SendAck(forSeqId);
        }

        /// <summary>Initializes available client IDs.</summary>
        private void InitializeClientIds()
        {
            availableClientIds = new List<ushort>(MaxClientCount);
            for (ushort i = 1; i <= MaxClientCount; i++)
                availableClientIds.Add(i);
        }

        /// <summary>Retrieves an available client ID.</summary>
        /// <returns>The client ID. 0 if none available.</returns>
        private ushort GetAvailableClientId()
        {
            if (availableClientIds.Count > 0)
            {
                ushort id = availableClientIds[0];
                availableClientIds.RemoveAt(0);
                return id;
            }
            else
            {
                RiptideLogger.Log(LogName, "No available client IDs, assigned 0!");
                return 0;
            }
        }

        /// <summary>Sends a message to a specific client.</summary>
        /// <param name="message">The message to send.</param>
        /// <param name="toClient">The client to send the message to.</param>
        /// <param name="maxSendAttempts">How often to try sending a reliable message before giving up.</param>
        /// <param name="shouldRelease">Whether or not <paramref name="message"/> should be returned to the pool once its data has been sent.</param>
        public void Send(Message message, ServerClient toClient, byte maxSendAttempts = 15, bool shouldRelease = true)
        {
            if (message.SendMode == MessageSendMode.unreliable)
                Send(message.Bytes, message.WrittenLength, toClient.remoteEndPoint);
            else
                SendReliable(message, toClient.remoteEndPoint, toClient.Rudp, maxSendAttempts);

            if (shouldRelease)
                message.Release();
        }

        /// <summary>Sends a message to all conected clients.</summary>
        /// <param name="message">The message to send.</param>
        /// <param name="maxSendAttempts">How often to try sending a reliable message before giving up.</param>
        /// <param name="shouldRelease">Whether or not <paramref name="message"/> should be returned to the pool once its data has been sent.</param>
        public void SendToAll(Message message, byte maxSendAttempts = 15, bool shouldRelease = true)
        {
            lock (clients)
            {
                if (message.SendMode == MessageSendMode.unreliable)
                {
                    foreach (IPEndPoint clientEndPoint in clients.Keys)
                        Send(message.Bytes, message.WrittenLength, clientEndPoint);
                }
                else
                {
                    foreach (ServerClient client in clients.Values)
                        SendReliable(message, client.remoteEndPoint, client.Rudp, maxSendAttempts);
                }
            }

            if (shouldRelease)
                message.Release();
        }

        /// <summary>Sends a message to all connected clients except one.</summary>
        /// <param name="message">The message to send.</param>
        /// <param name="exceptToClient">The client NOT to send the message to.</param>
        /// <param name="maxSendAttempts">How often to try sending a reliable message before giving up.</param>
        /// <param name="shouldRelease">Whether or not <paramref name="message"/> should be returned to the pool once its data has been sent.</param>
        public void SendToAll(Message message, ServerClient exceptToClient, byte maxSendAttempts = 15, bool shouldRelease = true)
        {
            lock (clients)
            {
                if (message.SendMode == MessageSendMode.unreliable)
                {
                    foreach (IPEndPoint clientEndPoint in clients.Keys)
                        if (!clientEndPoint.Equals(exceptToClient.remoteEndPoint))
                            Send(message.Bytes, message.WrittenLength, clientEndPoint);
                }
                else
                {
                    foreach (ServerClient client in clients.Values)
                        if (!client.remoteEndPoint.Equals(exceptToClient.remoteEndPoint))
                            SendReliable(message, client.remoteEndPoint, client.Rudp, maxSendAttempts);
                }
            }

            if (shouldRelease)
                message.Release();
        }

        /// <summary>Kicks a specific client.</summary>
        /// <param name="client">The client to kick.</param>
        public void DisconnectClient(ServerClient client)
        {
            if (clients.ContainsKey(client.remoteEndPoint))
            {
                SendDisconnect(client);
                client.Disconnect();
                lock (clients)
                    clients.Remove(client.remoteEndPoint);

                if (ShouldOutputInfoLogs)
                    RiptideLogger.Log(LogName, $"Kicked {client.remoteEndPoint}.");
                OnClientDisconnected(new ClientDisconnectedEventArgs(client.Id));

                availableClientIds.Add(client.Id);
            }
            else
                RiptideLogger.Log(LogName, $"Failed to kick {client.remoteEndPoint} because they weren't connected.");
        }
        
        /// <summary>Stops the server.</summary>
        public void Stop()
        {
            byte[] disconnectBytes = { (byte)HeaderType.disconnect };
            lock (clients)
            {
                foreach (IPEndPoint clientEndPoint in clients.Keys)
                    Send(disconnectBytes, clientEndPoint);
                clients.Clear();
            }

            IsRunning = false;
            heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
            heartbeatTimer.Dispose();
            StopListening();

            if (ShouldOutputInfoLogs)
                RiptideLogger.Log(LogName, "Server stopped.");
        }

        #region Messages
        /// <summary>Sends a disconnect message.</summary>
        /// <param name="client">The client to send the disconnect message to.</param>
        private void SendDisconnect(ServerClient client)
        {
            Send(Message.Create(HeaderType.disconnect), client);
        }

        /// <summary>Handles a disconnect message.</summary>
        /// <param name="fromEndPoint">The endpoint from which the disconnect message was received.</param>
        private void HandleDisconnect(IPEndPoint fromEndPoint)
        {
            if (clients.TryGetValue(fromEndPoint, out ServerClient client))
            {
                client.Disconnect();
                lock (clients)
                    clients.Remove(fromEndPoint);
                OnClientDisconnected(new ClientDisconnectedEventArgs(client.Id));

                availableClientIds.Add(client.Id);
            }
        }

        /// <summary>Sends a client connected message.</summary>
        /// <param name="endPoint">The endpoint of the newly connected client.</param>
        /// <param name="id">The ID of the newly connected client.</param>
        private void SendClientConnected(IPEndPoint endPoint, ushort id)
        {
            Message message = Message.Create(HeaderType.clientConnected);
            message.Add(id);

            lock (clients)
            {
                foreach (ServerClient client in clients.Values)
                {
                    if (!client.remoteEndPoint.Equals(endPoint))
                        Send(message, client, 25);
                    else if (clients.Count == 1)
                        message.Release();
                }
            }       
        }

        /// <summary>Sends a client disconnected message.</summary>
        /// <param name="id">The ID of the client that disconnected.</param>
        private void SendClientDisconnected(ushort id)
        {
            Message message = Message.Create(HeaderType.clientDisconnected);
            message.Add(id);

            lock (clients)
                foreach (ServerClient client in clients.Values)
                    Send(message, client, 25);
        }
        #endregion

        #region Events
        /// <summary>Invoked when a new client connects.</summary>
        public event EventHandler<ServerClientConnectedEventArgs> ClientConnected;
        internal void OnClientConnected(ServerClientConnectedEventArgs e)
        {
            if (ShouldOutputInfoLogs)
                RiptideLogger.Log(LogName, $"{e.Client.remoteEndPoint} connected successfully! Client ID: {e.Client.Id}");

            receiveActionQueue.Add(() => ClientConnected?.Invoke(this, e));

            SendClientConnected(e.Client.remoteEndPoint, e.Client.Id);
        }
        /// <summary>Invoked when a message is received from a client.</summary>
        public event EventHandler<ServerMessageReceivedEventArgs> MessageReceived;
        internal void OnMessageReceived(ServerMessageReceivedEventArgs e)
        {
            MessageReceived?.Invoke(this, e);
        }
        /// <summary>Invoked when a client disconnects.</summary>
        public event EventHandler<ClientDisconnectedEventArgs> ClientDisconnected;
        internal void OnClientDisconnected(ClientDisconnectedEventArgs e)
        {
            if (ShouldOutputInfoLogs)
                RiptideLogger.Log(LogName, $"Client {e.Id} has disconnected.");

            receiveActionQueue.Add(() => ClientDisconnected?.Invoke(this, e));
            
            SendClientDisconnected(e.Id);
        }
        #endregion
    }
}
