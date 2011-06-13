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
using System.Diagnostics;
using System.IO;
using System.Net;
using Org.BouncyCastle.Math;

namespace BitCoinSharp.Examples
{
    /// <summary>
    /// PingService demonstrates basic usage of the library. It sits on the network and when it receives coins, simply
    /// sends them right back to the previous owner, determined rather arbitrarily by the address of the first input.
    /// </summary>
    public static class PingService
    {
        public static void Run(string[] args)
        {
            var testNet = args.Length > 0 && string.Equals(args[0], "testnet", StringComparison.InvariantCultureIgnoreCase);
            var @params = testNet ? NetworkParameters.TestNet() : NetworkParameters.ProdNet();
            var filePrefix = testNet ? "pingservice-testnet" : "pingservice-prodnet";

            // Try to read the wallet from storage, create a new one if not possible.
            Wallet wallet;
            var walletFile = new FileInfo(filePrefix + ".wallet");
            try
            {
                wallet = Wallet.LoadFromFile(walletFile);
            }
            catch (IOException)
            {
                wallet = new Wallet(@params);
                wallet.Keychain.Add(new EcKey());
                wallet.SaveToFile(walletFile);
            }
            // Fetch the first key in the wallet (should be the only key).
            var key = wallet.Keychain[0];

            // Load the block chain, if there is one stored locally.
            Console.WriteLine("Reading block store from disk");
            using (var blockStore = new BoundedOverheadBlockStore(@params, new FileInfo(filePrefix + ".blockchain")))
            {
                // Connect to the localhost node. One minute timeout since we won't try any other peers
                Console.WriteLine("Connecting ...");
                using (var conn = new NetworkConnection(IPAddress.Loopback, @params, blockStore.GetChainHead().Height, 60000))
                {
                    var chain = new BlockChain(@params, wallet, blockStore);
                    var peer = new Peer(@params, conn, chain);
                    peer.Start();

                    // We want to know when the balance changes.
                    wallet.CoinsReceived +=
                        (sender, e) =>
                        {
                            // Running on a peer thread.
                            Debug.Assert(!e.NewBalance.Equals(BigInteger.Zero));
                            // It's impossible to pick one specific identity that you receive coins from in BitCoin as there
                            // could be inputs from many addresses. So instead we just pick the first and assume they were all
                            // owned by the same person.
                            var input = e.Tx.Inputs[0];
                            var from = input.FromAddress;
                            var value = e.Tx.GetValueSentToMe(wallet);
                            Console.WriteLine("Received " + Utils.BitcoinValueToFriendlyString(value) + " from " + from);
                            // Now send the coins back!
                            var sendTx = wallet.SendCoins(peer, from, value);
                            Debug.Assert(sendTx != null); // We should never try to send more coins than we have!
                            Console.WriteLine("Sent coins back! Transaction hash is " + sendTx.HashAsString);
                            wallet.SaveToFile(walletFile);
                        };

                    var progress = peer.StartBlockChainDownload();
                    var max = progress.Count; // Racy but no big deal.
                    if (max > 0)
                    {
                        Console.WriteLine("Downloading block chain. " + (max > 1000 ? "This may take a while." : ""));
                        var current = max;
                        while (current > 0)
                        {
                            var pct = 100.0 - (100.0*(current/(double) max));
                            Console.WriteLine("Chain download {0}% done", (int) pct);
                            progress.Await(TimeSpan.FromSeconds(1));
                            current = progress.Count;
                        }
                    }
                    Console.WriteLine("Send coins to: " + key.ToAddress(@params));
                    Console.WriteLine("Waiting for coins to arrive. Press Ctrl-C to quit.");
                    // The peer thread keeps us alive until something kills the process.
                }
            }
        }
    }
}