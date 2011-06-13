/*
 * Copyright 2011 Google Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *    http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BitCoinSharp.Common;
using BitCoinSharp.IO;
using log4net;

namespace BitCoinSharp
{
    /// <summary>
    /// A NetworkConnection handles talking to a remote BitCoin peer at a low level. It understands how to read and write
    /// messages off the network, but doesn't asynchronously communicate with the peer or handle the higher level details
    /// of the protocol. After constructing a NetworkConnection, use a <see cref="Peer">Peer</see> to hand off communication to a
    /// background thread.
    /// </summary>
    /// <remarks>
    /// Construction is blocking whilst the protocol version is negotiated.
    /// </remarks>
    public class NetworkConnection : IDisposable
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof (NetworkConnection));

        private const int _commandLen = 12;

        // Message strings.
        internal const string MsgVersion = "version";
        internal const string MsgInventory = "inv";
        internal const string MsgBlock = "block";
        internal const string MsgGetblocks = "getblocks";
        internal const string MsgGetdata = "getdata";
        internal const string MsgTx = "tx";
        internal const string MsgAddr = "addr";
        internal const string MsgVerack = "verack";

        private Socket _socket;
        private Stream _out;
        private Stream _in;
        // The IP address to which we are connecting.
        private readonly IPAddress _remoteIp;
        private readonly bool _usesChecksumming;
        private readonly NetworkParameters _params;
        private readonly VersionMessage _versionMessage;

        /// <summary>
        /// Connect to the given IP address using the port specified as part of the network parameters. Once construction
        /// is complete a functioning network channel is set up and running.
        /// </summary>
        /// <param name="remoteIp">IP address to connect to. IPv6 is not currently supported by BitCoin.</param>
        /// <param name="params">Defines which network to connect to and details of the protocol.</param>
        /// <param name="bestHeight">How many blocks are in our best chain</param>
        /// <param name="connectTimeout">Timeout in milliseconds when initially connecting to peer</param>
        /// <exception cref="System.IO.IOException">if there is a network related failure.</exception>
        /// <exception cref="ProtocolException">if the version negotiation failed.</exception>
        /// <exception cref="BitCoinSharp.ProtocolException" />
        public NetworkConnection(IPAddress remoteIp, NetworkParameters @params, int bestHeight, int connectTimeout)
        {
            _params = @params;
            _remoteIp = remoteIp;

            var address = new IPEndPoint(remoteIp, @params.Port);
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _socket.Connect(address);
            _socket.SendTimeout = _socket.ReceiveTimeout = connectTimeout;

            _out = new NetworkStream(_socket, FileAccess.Write);
            _in = new NetworkStream(_socket, FileAccess.Read);

            // Announce ourselves. This has to come first to connect to clients beyond v0.30.20.2 which wait to hear
            // from us until they send their version message back.
            WriteMessage(MsgVersion, new VersionMessage(@params, bestHeight));
            // When connecting, the remote peer sends us a version message with various bits of
            // useful data in it. We need to know the peer protocol version before we can talk to it.
            _versionMessage = (VersionMessage) ReadMessage();
            // Now it's our turn ...
            // Send an ACK message stating we accept the peers protocol version.
            WriteMessage(MsgVerack, new byte[] {});
            // And get one back ...
            ReadMessage();
            // Switch to the new protocol version.
            var peerVersion = _versionMessage.ClientVersion;
            _log.InfoFormat("Connected to peer: version={0}, subVer='{1}', services=0x{2}, time={3}, blocks={4}",
                            peerVersion,
                            _versionMessage.SubVer,
                            _versionMessage.LocalServices,
                            UnixTime.FromUnixTime(_versionMessage.Time),
                            _versionMessage.BestHeight
                );
            // BitCoinSharp is a client mode implementation. That means there's not much point in us talking to other client
            // mode nodes because we can't download the data from them we need to find/verify transactions.
            if (!_versionMessage.HasBlockChain())
                throw new ProtocolException("Peer does not have a copy of the block chain.");
            _usesChecksumming = peerVersion >= 209;
            // Handshake is done!
        }

        /// <summary>
        /// Sends a "ping" message to the remote node. The protocol doesn't presently use this feature much.
        /// </summary>
        /// <exception cref="System.IO.IOException">System.IO.IOException</exception>
        public void Ping()
        {
            WriteMessage("ping", new byte[] {});
        }

        /// <summary>
        /// Shuts down the network socket. Note that there's no way to wait for a socket to be fully flushed out to the
        /// wire, so if you call this immediately after sending a message it might not get sent.
        /// </summary>
        /// <exception cref="System.IO.IOException" />
        public void Shutdown()
        {
            _socket.Disconnect(false);
            _socket.Close();
        }

        public override string ToString()
        {
            return "[" + _remoteIp + "]:" + _params.Port + " (" + (_socket.Connected ? "connected" : "disconnected") + ")";
        }

        /// <exception cref="System.IO.IOException" />
        private void SeekPastMagicBytes()
        {
            var magicCursor = 3; // Which byte of the magic we're looking for currently.
            while (true)
            {
                var b = _in.Read(); // Read a byte.
                if (b == -1)
                {
                    // There's no more data to read.
                    throw new IOException("Socket is disconnected");
                }
                // We're looking for a run of bytes that is the same as the packet magic but we want to ignore partial
                // magics that aren't complete. So we keep track of where we're up to with magicCursor.
                var expectedByte = 0xFF & (int) (_params.PacketMagic >> magicCursor*8);
                if (b == expectedByte)
                {
                    magicCursor--;
                    if (magicCursor < 0)
                    {
                        // We found the magic sequence.
                        return;
                    }
                }
                else
                {
                    // We still have further to go to find the next message.
                    magicCursor = 3;
                }
            }
        }

        /// <summary>
        /// Reads a network message from the wire, blocking until the message is fully received.
        /// </summary>
        /// <returns>An instance of a Message subclass</returns>
        /// <exception cref="ProtocolException">if the message is badly formatted, failed checksum or there was a TCP failure.</exception>
        /// <exception cref="System.IO.IOException" />
        /// <exception cref="BitCoinSharp.ProtocolException" />
        public Message ReadMessage()
        {
            // A BitCoin protocol message has the following format.
            //
            //   - 4 byte magic number: 0xfabfb5da for the testnet or
            //                          0xf9beb4d9 for production
            //   - 12 byte command in ASCII
            //   - 4 byte payload size
            //   - 4 byte checksum
            //   - Payload data
            //
            // The checksum is the first 4 bytes of a SHA256 hash of the message payload. It isn't
            // present for all messages, notably, the first one on a connection.
            //
            // Satoshi's implementation ignores garbage before the magic header bytes. We have to do the same because
            // sometimes it sends us stuff that isn't part of any message.
            SeekPastMagicBytes();
            // Now read in the header.
            var header = new byte[_commandLen + 4 + (_usesChecksumming ? 4 : 0)];
            var readCursor = 0;
            while (readCursor < header.Length)
            {
                var bytesRead = _in.Read(header, readCursor, header.Length - readCursor);
                if (bytesRead == -1)
                {
                    // There's no more data to read.
                    throw new IOException("Socket is disconnected");
                }
                readCursor += bytesRead;
            }

            var cursor = 0;

            // The command is a NULL terminated string, unless the command fills all twelve bytes
            // in which case the termination is implicit.
            var mark = cursor;
            for (; header[cursor] != 0 && cursor - mark < _commandLen; cursor++)
            {
            }
            var commandBytes = new byte[cursor - mark];
            Array.Copy(header, mark, commandBytes, 0, cursor - mark);
            var command = Encoding.UTF8.GetString(commandBytes, 0, commandBytes.Length);
            cursor = mark + _commandLen;

            var size = (int) Utils.ReadUint32(header, cursor);
            cursor += 4;

            if (size > Message.MaxSize)
                throw new ProtocolException("Message size too large: " + size);

            // Old clients don't send the checksum.
            var checksum = new byte[4];
            if (_usesChecksumming)
            {
                // Note that the size read above includes the checksum bytes.
                Array.Copy(header, cursor, checksum, 0, 4);
            }

            // Now try to read the whole message.
            readCursor = 0;
            var payloadBytes = new byte[size];
            while (readCursor < payloadBytes.Length - 1)
            {
                var bytesRead = _in.Read(payloadBytes, readCursor, size - readCursor);
                if (bytesRead == -1)
                {
                    throw new IOException("Socket is disconnected");
                }
                readCursor += bytesRead;
            }

            // Verify the checksum.
            if (_usesChecksumming)
            {
                var hash = Utils.DoubleDigest(payloadBytes);
                if (checksum[0] != hash[0] || checksum[1] != hash[1] || checksum[2] != hash[2] ||
                    checksum[3] != hash[3])
                {
                    throw new ProtocolException("Checksum failed to verify, actual " +
                                                Utils.BytesToHexString(hash) +
                                                " vs " + Utils.BytesToHexString(checksum));
                }
            }

            if (_log.IsDebugEnabled)
            {
                _log.DebugFormat("Received {0} byte '{1}' message: {2}",
                                 size,
                                 command,
                                 Utils.BytesToHexString(payloadBytes)
                    );
            }
            try
            {
                Message message;
                if (command.Equals(MsgVersion))
                    message = new VersionMessage(_params, payloadBytes);
                else if (command.Equals(MsgInventory))
                    message = new InventoryMessage(_params, payloadBytes);
                else if (command.Equals(MsgBlock))
                    message = new Block(_params, payloadBytes);
                else if (command.Equals(MsgGetdata))
                    message = new GetDataMessage(_params, payloadBytes);
                else if (command.Equals(MsgTx))
                    message = new Transaction(_params, payloadBytes);
                else if (command.Equals(MsgAddr))
                    message = new AddressMessage(_params, payloadBytes);
                else
                    message = new UnknownMessage(_params, command, payloadBytes);
                return message;
            }
            catch (Exception e)
            {
                throw new ProtocolException("Error deserializing message " + Utils.BytesToHexString(payloadBytes) + "\n", e);
            }
        }

        /// <exception cref="System.IO.IOException" />
        private void WriteMessage(string name, byte[] payload)
        {
            var header = new byte[4 + _commandLen + 4 + (_usesChecksumming ? 4 : 0)];

            Utils.Uint32ToByteArrayBe(_params.PacketMagic, header, 0);

            // The header array is initialized to zero by Java so we don't have to worry about
            // NULL terminating the string here.
            for (var i = 0; i < name.Length && i < _commandLen; i++)
            {
                header[4 + i] = (byte) (name[0] & 0xFF);
            }

            Utils.Uint32ToByteArrayLe(payload.Length, header, 4 + _commandLen);

            if (_usesChecksumming)
            {
                var hash = Utils.DoubleDigest(payload);
                Array.Copy(hash, 0, header, 4 + _commandLen + 4, 4);
            }

            _log.DebugFormat("Sending {0} message: {1}", name, Utils.BytesToHexString(payload));

            // Another writeMessage call may be running concurrently.
            lock (_out)
            {
                _out.Write(header);
                _out.Write(payload);
            }
        }

        /// <summary>
        /// Writes the given message out over the network using the protocol tag. For a Transaction
        /// this should be "tx" for example. It's safe to call this from multiple threads simultaneously,
        /// the actual writing will be serialized.
        /// </summary>
        /// <exception cref="System.IO.IOException">System.IO.IOException</exception>
        public void WriteMessage(string tag, Message message)
        {
            // TODO: Requiring "tag" here is redundant, the message object should know its own protocol tag.
            WriteMessage(tag, message.BitcoinSerialize());
        }

        /// <summary>
        /// Returns the version message received from the other end of the connection during the handshake.
        /// </summary>
        public VersionMessage VersionMessage
        {
            get { return _versionMessage; }
        }

        #region IDisposable Members

        public void Dispose()
        {
            if (_in != null)
            {
                _in.Dispose();
                _in = null;
            }
            if (_out != null)
            {
                _out.Dispose();
                _out = null;
            }
            if (_socket != null)
            {
                ((IDisposable) _socket).Dispose();
                _socket = null;
            }
        }

        #endregion
    }
}