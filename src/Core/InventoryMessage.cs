/**
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
using System.Collections.Generic;
using System.IO;
using BitCoinSharp.IO;

namespace BitCoinSharp
{
    [Serializable]
    public class InventoryMessage : Message
    {
        private const long _maxInventoryItems = 50000;

        // For some reason the compiler complains if this is inside InventoryItem
        public IList<InventoryItem> Items { get; private set; }

        /// <exception cref="BitCoinSharp.ProtocolException" />
        public InventoryMessage(NetworkParameters @params, byte[] bytes)
            : base(@params, bytes, 0)
        {
        }

        /// <exception cref="BitCoinSharp.ProtocolException" />
        protected override void Parse()
        {
            // An inv is vector<CInv> where CInv is int+hash. The int is either 1 or 2 for tx or block.
            var arrayLen = ReadVarInt();
            if (arrayLen > _maxInventoryItems)
                throw new ProtocolException("Too many items in INV message: " + arrayLen);
            Items = new List<InventoryItem>((int) arrayLen);
            for (var i = 0; i < arrayLen; i++)
            {
                if (Cursor + 4 + 32 > Bytes.Length)
                {
                    throw new ProtocolException("Ran off the end of the INV");
                }
                var typeCode = (int) ReadUint32();
                InventoryItem.ItemType type;
                // See ppszTypeName in net.h
                switch (typeCode)
                {
                    case 0:
                        type = InventoryItem.ItemType.Error;
                        break;
                    case 1:
                        type = InventoryItem.ItemType.Transaction;
                        break;
                    case 2:
                        type = InventoryItem.ItemType.Block;
                        break;
                    default:
                        throw new ProtocolException("Unknown CInv type: " + typeCode);
                }
                var item = new InventoryItem(type, ReadHash());
                Items.Add(item);
            }
            Bytes = null;
        }

        public InventoryMessage(NetworkParameters @params)
            : base(@params)
        {
            Items = new List<InventoryItem>();
        }

        /// <exception cref="System.IO.IOException" />
        public override void BitcoinSerializeToStream(Stream stream)
        {
            stream.Write(new VarInt(Items.Count).Encode());
            foreach (var i in Items)
            {
                // Write out the type code.
                Utils.Uint32ToByteStreamLe((int) i.Type, stream);
                // And now the hash.
                stream.Write(Utils.ReverseBytes(i.Hash));
            }
        }
    }
}