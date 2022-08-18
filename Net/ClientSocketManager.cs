using System.Collections.Generic;
using UnityEngine;

namespace Net
{
    public delegate void MessageHandle(int id, byte[] data);

    public class Message
    {
        public const int InvalidMessage = -1;

        public int messageId;
        public int messageLength;
        public byte[] content;
    }

    public delegate void ClientSocketConnectResultHandle(int socketId, string ip, int port);

    public class ClientSocketManager : MonoBehaviour
    {
        #region Singleton

        private static ClientSocketManager _manager;

        public static ClientSocketManager sharedInstance
        {
            get
            {
                if (_manager == null)
                {
                    _manager = FindObjectOfType<ClientSocketManager>();
                    if (_manager == null)
                    {
                        var go = new GameObject("Client Socket Manager");
                        _manager = go.AddComponent<ClientSocketManager>();
                        DontDestroyOnLoad(go);
                    }
                }

                return _manager;
            }
        }

        #endregion

        public event MessageHandle MessageHandle;

        private const int MessageHandleCount = 20;

        private readonly Queue<Message> _messagePool = new Queue<Message>();
        private readonly Queue<Message> _messageQueue = new Queue<Message>();

        private readonly Dictionary<int, ClientSocket> _clientSocketDict = new Dictionary<int, ClientSocket>();

        public event ClientSocketConnectResultHandle OnConnectSuccess, OnConnectFailure;

        public void Connect(int socketId, string ip, int port, int timeoutMilliseconds = 5000,
            bool isLittleEndian = false)
        {
            if (!_clientSocketDict.TryGetValue(socketId, out var clientSocket))
            {
                _clientSocketDict.Add(socketId, clientSocket = new ClientSocket(isLittleEndian, MessageFactory));
                clientSocket.OnRecvMessage += OnClientSocketRecvMessage;
            }

            if (clientSocket.status == ClientSocket.Status.Disconnected)
                clientSocket.Connect(ip, port, timeoutMilliseconds,
                    () => OnConnectSuccess?.Invoke(socketId, ip, port),
                    () => OnConnectFailure?.Invoke(socketId, ip, port), 5);
        }

        public void Close(int socketId)
        {
            if (_clientSocketDict.TryGetValue(socketId, out var clientSocket))
                clientSocket.Close();
        }

        public void SendMessage(int socketId, short messageId, byte[] data)
        {
            if (_clientSocketDict.TryGetValue(socketId, out var clientSocket))
            {
                clientSocket.SendMessage(messageId, data);
                return;
            }

            Debug.LogError($"Failed to send message with socketId:{socketId} !!!");
        }

        void Update()
        {
            var count = 0;
            while (_messageQueue.Count > 0 && count++ < MessageHandleCount)
            {
                var message = _messageQueue.Dequeue();
                if (MessageHandle != null)
                    MessageHandle(message.messageId, message.content);
                _messagePool.Enqueue(message);
                var content = message.content;
                message.content = Bytes.Empty;
                message.messageId = Message.InvalidMessage;
                message.messageLength = 0;
                Bytes.Dealloc(content);
            }
        }

        public Message MessageFactory() => _messagePool.Count > 0 ? _messagePool.Dequeue() : new Message();

        private void OnClientSocketRecvMessage(Message message) =>
            _messageQueue.Enqueue(message);
    }
}