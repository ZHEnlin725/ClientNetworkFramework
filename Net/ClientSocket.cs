using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using UnityEngine;

namespace Net
{
    public static class Bytes
    {
        public static readonly byte[] Empty = new byte[0];
        private static readonly Dictionary<int, Queue<byte[]>> ReusableBytes = new Dictionary<int, Queue<byte[]>>();

        public static byte[] Alloc(int size)
        {
            if (!ReusableBytes.TryGetValue(size, out var queue))
                ReusableBytes.Add(size, queue = new Queue<byte[]>(8));
            return queue.Count > 0 ? queue.Dequeue() : new byte[size];
        }

        public static void Dealloc(byte[] bytes)
        {
            if (bytes == null) return;
            var size = bytes.Length;
            if (!ReusableBytes.TryGetValue(size, out var queue))
                ReusableBytes.Add(size, queue = new Queue<byte[]>(8));
            queue.Enqueue(bytes);
        }

        public static void Clear() => ReusableBytes.Clear();
    }

    public delegate void ClientSocketMessageHandle(Message message);

    public class ClientSocket
    {
        public enum Status : byte
        {
            Disconnected,
            Connecting,
            Connected,
        }

        private struct MessageHead
        {
            public short messageId;
            public int messageLength;

            public const int Size = sizeof(short) + sizeof(int);

            public MessageHead(short messageId, int messageLength)
            {
                this.messageId = messageId;
                this.messageLength = messageLength;
            }

            public void Serialize(DataWriter writer)
            {
                writer.Write(messageId);
                writer.Write(messageLength);
            }

            public void Deserialize(DataReader reader)
            {
                messageId = reader.ReadInt16();
                messageLength = reader.ReadInt32();
            }
        }

        private sealed class DataReader
        {
            public int pointer;
            public int readSize;
            public bool isLittleEndian;
            public readonly byte[] buffer = new byte[65536];

            public Byte ReadByte()
            {
                if (pointer >= buffer.Length)
                {
                    Debug.LogError($"Out Of Index Read Byte curPoint:{pointer},buffLen:{buffer.Length}!!!");
                    return 0;
                }

                return buffer[pointer++];
            }

            public Int16 ReadInt16()
            {
                if (pointer >= buffer.Length || pointer + 2 > buffer.Length)
                {
                    Debug.LogError($"Out Of Index Read Int16 curPoint:{pointer},buffLen:{buffer.Length}!!!");
                    return 0;
                }

                short result = 0;
                for (var i = 0; i < 2; i++)
                    result |= (short) (buffer[isLittleEndian ? i + pointer : 2 - (i + 1) + pointer] << 8 * i);
                pointer += 2;
                return result;
            }

            public Int32 ReadInt32()
            {
                if (pointer >= buffer.Length || pointer + 4 > buffer.Length)
                {
                    Debug.LogError($"Out Of Index Read Int32 curPoint:{pointer},buffLen:{buffer.Length}!!!");
                    return 0;
                }

                var result = 0;
                for (var i = 0; i < 4; i++)
                    result |= buffer[isLittleEndian ? i + pointer : 4 - (i + 1) + pointer] << 8 * i;
                pointer += 4;
                return result;
            }

            public byte[] ReadBytes(int length)
            {
                if (length < 0) return Bytes.Empty;

                if (pointer >= buffer.Length || pointer + length > buffer.Length)
                {
                    Debug.LogError(
                        $"Out Of Index Read Byte curPoint:{pointer},bytesLen:{length},buffLen:{buffer.Length}!!!");
                    return Bytes.Empty;
                }

                var bytes = Bytes.Alloc(length);
                Buffer.BlockCopy(buffer, pointer, bytes, 0, length);
                pointer += length;
                return bytes;
            }

            public MessageHead ReadMessageHead()
            {
                var messageHead = new MessageHead();
                messageHead.Deserialize(this);
                return messageHead;
            }
        }

        private sealed class DataWriter
        {
            public int pointer;
            public bool isLittleEndian;
            public byte[] buffer = new byte[128];

            public DataWriter Write(byte val)
            {
                EnsureCapacity(pointer + sizeof(byte));
                buffer[pointer++] = val;
                return this;
            }

            public DataWriter Write(short val)
            {
                EnsureCapacity(pointer + sizeof(short));
                for (var i = 0; i < 2; i++)
                    buffer[isLittleEndian ? i + pointer : 2 - (i + 1) + pointer] =
                        (byte) ((val >> (8 * i)) & 0x0ff);
                pointer += 2;
                return this;
            }

            public DataWriter Write(int val)
            {
                EnsureCapacity(pointer + sizeof(int));
                for (var i = 0; i < 4; i++)
                    buffer[isLittleEndian ? i + pointer : 4 - (i + 1) + pointer] =
                        (byte) ((val >> (8 * i)) & 0x0ff);
                pointer += 4;
                return this;
            }

            public DataWriter Write(byte[] bytes)
            {
                var length = bytes.Length;
                EnsureCapacity(pointer + length);
                Buffer.BlockCopy(bytes, 0, buffer, pointer, length);
                pointer += length;
                return this;
            }

            public DataWriter Write(MessageHead messageHead)
            {
                messageHead.Serialize(this);
                return this;
            }

            private void EnsureCapacity(int size)
            {
                while (size > buffer.Length)
                {
                    var newSize = buffer.Length << 1 + 1;
                    var bytes = Bytes.Alloc(newSize);
                    Buffer.BlockCopy(buffer, 0, bytes, 0, buffer.Length);
                    var oldBuffer = buffer;
                    buffer = bytes;
                    Bytes.Dealloc(oldBuffer);
                }
            }
        }

        public int port;
        public string ip;
        public int counter;

        public Socket socket;
        public Status status = Status.Disconnected;
        public event ClientSocketMessageHandle OnRecvMessage;

        private DataReader _reader;
        private DataWriter _writer;

        private readonly Func<Message> _messageFunc;

        private readonly AsyncCallback _cachedConnectAsyncCallback;
        private readonly AsyncCallback _cachedRecvMessageHeadAsyncCallback;
        private readonly AsyncCallback _cachedRecvMessageContentAsyncCallback;

        public ClientSocket(bool isLittleEndian, Func<Message> messageFactory)
        {
            _reader = new DataReader {isLittleEndian = isLittleEndian};
            _writer = new DataWriter {isLittleEndian = isLittleEndian};
            _messageFunc = messageFactory;
            _cachedConnectAsyncCallback = ConnectAsyncCallback;
            _cachedRecvMessageHeadAsyncCallback = RecvMessageHeadAsyncCallback;
            _cachedRecvMessageContentAsyncCallback = RecvMessageContentAsyncCallback;
        }

        public void Connect(string ip, int port, int timeout, Action onSuccess, Action onFailure,
            int maxReconnectCount = 1)
        {
            counter = 0;
            BeginConnect(ip, port, timeout, onSuccess, onFailure, maxReconnectCount);
        }

        public void SendMessage(short messageId, byte[] content)
        {
            _writer.pointer = 0;
            var send = socket.Send(_writer.Write(messageId).Write(content?.Length ?? 0).Write(content).buffer, 0,
                _writer.pointer, SocketFlags.None);

#if ENABLE_DEBUG
                Debug.Log(
                    $"-----------[SEND MSG] MSG_ID:{messageId},Content:{content?.Length ?? -1} {send}-----------");
#endif
        }

        public void Update()
        {
        }

        public void Close()
        {
            if (socket != null) socket.Close();
            status = Status.Disconnected;
        }

        private void BeginConnect(string ip, int port, int timeout, Action onSuccess, Action onFailure,
            int maxReconnectCount)
        {
            BeginConnect(ip, port, timeout, onSuccess, () =>
            {
                if (counter++ < maxReconnectCount)
                {
#if ENABLE_DEBUG
                        Debug.LogError($"Try to reconnect ... count:{reconnectCount}!!!");
#endif
                    BeginConnect(ip, port, timeout, onSuccess, onFailure, maxReconnectCount);
                }
                else
                {
                    onFailure?.Invoke();
                }
            });
        }

        private void BeginConnect(string ip, int port, int timeout, Action onSuccess, Action onFailure)
        {
            if (status != Status.Disconnected) return;
            this.ip = ip;
            this.port = port;
            this.socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
#if ENABLE_DEBUG
                Debug.Log($"[Connect] Ip:{ip},port:{port}");
#endif
            CoroutineManager.sharedInstance.StartCoroutine(Connect(timeout, onSuccess, onFailure));
        }

        private IEnumerator Connect(int timeout, Action onSuccess, Action onFailure)
        {
            var error = string.Empty;
            IAsyncResult asyncResult = null;
            try
            {
                status = Status.Connecting;
                asyncResult = socket.BeginConnect(ip, port, _cachedConnectAsyncCallback, socket);
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            if (string.IsNullOrEmpty(error) && asyncResult != null)
            {
                for (var elapse = 0f;
                    elapse < timeout
                    && !asyncResult.IsCompleted
                    && status != Status.Disconnected;
                    elapse += Time.deltaTime * 1000)
                {
#if ENABLE_DEBUG
                        if (elapse >= timeout)
                            Debug.LogError("Connection Timeout !!!");
#endif
                    yield return null;
                }

                asyncResult.AsyncWaitHandle.Close();

                if (socket.Connected)
                {
                    BeginRecvMessage();
                    status = Status.Connected;
                    onSuccess?.Invoke();
                }
                else
                {
                    Close();
                    onFailure?.Invoke();
                }
            }
            else
            {
#if ENABLE_DEBUG
                    Debug.LogError($"Connect Error:{error}");
#endif
                Close();
                onFailure?.Invoke();
            }
        }

        private void BeginRecvMessage() =>
            socket.BeginReceive(_reader.buffer, _reader.readSize = 0, MessageHead.Size,
                SocketFlags.None, _cachedRecvMessageHeadAsyncCallback, null);

        private void RecvMessageHeadAsyncCallback(IAsyncResult ar)
        {
            var receive = socket.EndReceive(ar);
            if (receive == 0)
            {
                Close();
                return;
            }

            _reader.readSize += receive;
            var bytes = _reader.buffer;
            if (_reader.readSize == MessageHead.Size)
            {
                _reader.pointer = 0;
                var messageHead = _reader.ReadMessageHead();
                var message = _messageFunc != null ? _messageFunc() : new Message();
                message.messageId = messageHead.messageId;
                var messageLength = messageHead.messageLength;
                message.messageLength = messageLength;
                socket.BeginReceive(bytes, _reader.readSize = 0, messageLength,
                    SocketFlags.None, _cachedRecvMessageContentAsyncCallback, message);
            }
            else
            {
                socket.BeginReceive(bytes, _reader.readSize, MessageHead.Size - _reader.readSize, SocketFlags.None,
                    _cachedRecvMessageHeadAsyncCallback, null);
            }
        }

        private void RecvMessageContentAsyncCallback(IAsyncResult ar)
        {
            var receive = socket.EndReceive(ar);
            if (receive == 0)
            {
                Close();
                return;
            }

            _reader.readSize += receive;
            var message = (Message) ar.AsyncState;
            var messageLength = message.messageLength;
            var bytes = _reader.buffer;
            if (_reader.readSize == messageLength)
            {
                _reader.pointer = 0;
                message.content = messageLength > 0 ? _reader.ReadBytes(messageLength) : Bytes.Empty;
                //todo broadcast recv message
                if (OnRecvMessage != null)
                    OnRecvMessage(message);
                BeginRecvMessage();
            }
            else
            {
                socket.BeginReceive(bytes, _reader.readSize, messageLength - _reader.readSize,
                    SocketFlags.None, _cachedRecvMessageContentAsyncCallback, message);
            }
        }

        private void ConnectAsyncCallback(IAsyncResult ar) => ((Socket) ar.AsyncState).EndConnect(ar);
    }
}