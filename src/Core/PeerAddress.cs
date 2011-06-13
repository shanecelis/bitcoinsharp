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
using BitCoinSharp.Common;
using BitCoinSharp.IO;
using Org.BouncyCastle.Math;

namespace BitCoinSharp
{
    /// <summary>
    /// A PeerAddress holds an IP address and port number representing the network location of
    /// a peer in the BitCoin P2P network. It exists primarily for serialization purposes.
    /// </summary>
    [Serializable]
    public class PeerAddress : Message
    {
        private IPAddress _addr;
        private int _port;
        private BigInteger _services;
        private long _time;

        /// <exception cref="BitCoinSharp.ProtocolException" />
        public PeerAddress(NetworkParameters @params, byte[] payload, int offset, int protocolVersion)
            : base(@params, payload, offset, protocolVersion)
        {
        }

        public PeerAddress(IPAddress addr, int port, int protocolVersion)
        {
            _addr = addr;
            _port = port;
            ProtocolVersion = protocolVersion;
        }

        /// <exception cref="System.IO.IOException" />
        public override void BitcoinSerializeToStream(Stream stream)
        {
            if (ProtocolVersion >= 31402)
            {
                var secs = UnixTime.ToUnixTime(DateTime.UtcNow);
                Utils.Uint32ToByteStreamLe(secs, stream);
            }
            Utils.Uint64ToByteStreamLe(BigInteger.Zero, stream); // nServices.
            // Java does not provide any utility to map an IPv4 address into IPv6 space, so we have to do it by hand.
            var ipBytes = _addr.GetAddressBytes();
            if (ipBytes.Length == 4)
            {
                var v6Addr = new byte[16];
                Array.Copy(ipBytes, 0, v6Addr, 12, 4);
                v6Addr[10] = 0xFF;
                v6Addr[11] = 0xFF;
                ipBytes = v6Addr;
            }
            stream.Write(ipBytes);
            // And write out the port.
            stream.Write((byte) (0xFF & _port));
            stream.Write((byte) (0xFF & (_port >> 8)));
        }

        /// <exception cref="BitCoinSharp.ProtocolException" />
        protected override void Parse()
        {
            // Format of a serialized address:
            //   uint32 timestamp
            //   uint64 services   (flags determining what the node can do)
            //   16 bytes ip address
            //   2 bytes port num
            if (ProtocolVersion > 31402)
                _time = ReadUint32();
            else
                _time = -1;
            _services = ReadUint64();
            var addrBytes = ReadBytes(16);
            _addr = new IPAddress(addrBytes);
            _port = ((0xFF & Bytes[Cursor++]) << 8) | (0xFF & Bytes[Cursor++]);
        }

        public override string ToString()
        {
            return "[" + _addr + "]:" + _port;
        }
    }
}