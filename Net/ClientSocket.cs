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
        private const int MSG_ID_SIZE = sizeof(short);
        private const int MSG_HEAD_SIZE = sizeof(int);

        public enum Status : byte
        {
            Disconnected,
            Connecting,
            Connected,
        }

        private sealed class DataReader
        {
            public int pointer;
            public int recvSize;
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

            public Int16 ReadInt16() => (short) ReadFlexibleSize(2);

            public Int32 ReadInt32() => (int) ReadFlexibleSize(4);

            public Int64 ReadFlexibleSize(int size)
            {
                long result = 0;

                if (size > sizeof(long) || size <= 0)
                    return result;

                if (pointer >= buffer.Length || pointer + size > buffer.Length)
                {
                    Debug.LogError($"Out Of Index Read Length:{size} curPoint:{pointer},buffLen:{buffer.Length}!!!");
                    return 0;
                }

                for (var i = 0; i < size; i++)
                    result |= buffer[isLittleEndian ? i + pointer : size - (i + 1) + pointer] << 8 * i;
                pointer += size;

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
            var send = socket.Send(
                _writer.Write((content?.Length ?? 0) + MSG_ID_SIZE).Write(messageId).Write(content).buffer, 0,
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
            socket.BeginReceive(_reader.buffer, _reader.recvSize = 0, MSG_HEAD_SIZE,
                SocketFlags.None, _cachedRecvMessageHeadAsyncCallback, null);

        private void RecvMessageHeadAsyncCallback(IAsyncResult ar)
        {
            var receive = socket.EndReceive(ar);
            if (receive == 0)
            {
                Close();
                return;
            }

            _reader.recvSize += receive;
            var bytes = _reader.buffer;
            if (_reader.recvSize == MSG_HEAD_SIZE)
            {
                _reader.pointer = 0;
                var message = _messageFunc != null ? _messageFunc() : new Message();
                message.messageLength = (int) _reader.ReadFlexibleSize(MSG_HEAD_SIZE);
                socket.BeginReceive(bytes, _reader.recvSize = 0, message.messageLength,
                    SocketFlags.None, _cachedRecvMessageContentAsyncCallback, message);
            }
            else
            {
                socket.BeginReceive(bytes, _reader.recvSize, MSG_HEAD_SIZE - _reader.recvSize, SocketFlags.None,
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

            _reader.recvSize += receive;
            var message = (Message) ar.AsyncState;
            var messageLength = message.messageLength;
            var bytes = _reader.buffer;
            if (_reader.recvSize == messageLength)
            {
                _reader.pointer = 0;
                message.messageId = (int) _reader.ReadFlexibleSize(MSG_ID_SIZE);
                var length = messageLength - MSG_ID_SIZE;
                message.content = length > 0 ? _reader.ReadBytes(length) : Bytes.Empty;

                //todo broadcast recv message
                if (OnRecvMessage != null)
                    OnRecvMessage(message);
                BeginRecvMessage();
            }
            else
            {
                socket.BeginReceive(bytes, _reader.recvSize, messageLength - _reader.recvSize,
                    SocketFlags.None, _cachedRecvMessageContentAsyncCallback, message);
            }
        }

        private void ConnectAsyncCallback(IAsyncResult ar) => ((Socket) ar.AsyncState).EndConnect(ar);
    }
}