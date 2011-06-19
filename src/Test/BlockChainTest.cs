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
using BitCoinSharp.Common;
using NUnit.Framework;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Utilities.Encoders;

namespace BitCoinSharp.Test
{
    [TestFixture]
    public class BlockChainTest
    {
        private static readonly NetworkParameters _testNet = NetworkParameters.TestNet();
        private IBlockStore _testNetChainBlockStore;
        private BlockChain _testNetChain;

        private Wallet _wallet;
        private IBlockStore _chainBlockStore;
        private BlockChain _chain;
        private Address _coinbaseTo;
        private NetworkParameters _unitTestParams;
        private Address _someOtherGuy;

        // NOTE: Handling of chain splits/reorgs are in ChainSplitTests.
        [SetUp]
        public void SetUp()
        {
            _testNetChainBlockStore = new MemoryBlockStore(_testNet);
            _testNetChain = new BlockChain(_testNet, new Wallet(_testNet), _testNetChainBlockStore);

            _unitTestParams = NetworkParameters.UnitTests();
            _wallet = new Wallet(_unitTestParams);
            _wallet.AddKey(new EcKey());
            _chainBlockStore = new MemoryBlockStore(_unitTestParams);
            _chain = new BlockChain(_unitTestParams, _wallet, _chainBlockStore);

            _coinbaseTo = _wallet.Keychain[0].ToAddress(_unitTestParams);
            _someOtherGuy = new EcKey().ToAddress(_unitTestParams);
        }

        [TearDown]
        public void TearDown()
        {
            _testNetChainBlockStore.Dispose();
            _chainBlockStore.Dispose();
        }

        [Test]
        public void TestBasicChaining()
        {
            // Check that we can plug a few blocks together.
            // Block 1 from the testnet.
            var b1 = GetBlock1();
            Assert.True(_testNetChain.Add(b1));
            // Block 2 from the testnet.
            var b2 = GetBlock2();

            // Let's try adding an invalid block.
            var n = b2.Nonce;
            try
            {
                b2.Nonce = 12345;
                _testNetChain.Add(b2);
                Assert.Fail();
            }
            catch (VerificationException)
            {
                b2.Nonce = n;
            }
            // Now it works because we reset the nonce.
            Assert.True(_testNetChain.Add(b2));
        }

        [Test]
        public void TestUnconnectedBlocks()
        {
            var b1 = _unitTestParams.GenesisBlock.CreateNextBlock(_coinbaseTo);
            var b2 = b1.CreateNextBlock(_coinbaseTo);
            var b3 = b2.CreateNextBlock(_coinbaseTo);
            // Connected.
            Assert.True(_chain.Add(b1));
            // Unconnected but stored. The head of the chain is still b1.
            Assert.False(_chain.Add(b3));
            Assert.AreEqual(_chain.ChainHead.Header, b1.CloneAsHeader());
            // Add in the middle block.
            Assert.True(_chain.Add(b2));
            Assert.AreEqual(_chain.ChainHead.Header, b3.CloneAsHeader());
        }

        [Test]
        public void TestDifficultyTransitions()
        {
            // Add a bunch of blocks in a loop until we reach a difficulty transition point. The unit test params have an
            // artificially shortened period.
            var prev = _unitTestParams.GenesisBlock;
            Block.FakeClock = UnixTime.ToUnixTime(DateTime.UtcNow);
            for (var i = 0; i < _unitTestParams.Interval - 1; i++)
            {
                var newBlock = prev.CreateNextBlock(_coinbaseTo, (uint) Block.FakeClock);
                Assert.True(_chain.Add(newBlock));
                prev = newBlock;
                // The fake chain should seem to be "fast" for the purposes of difficulty calculations.
                Block.FakeClock += 2;
            }
            // Now add another block that has no difficulty adjustment, it should be rejected.
            try
            {
                _chain.Add(prev.CreateNextBlock(_coinbaseTo));
                Assert.Fail();
            }
            catch (VerificationException)
            {
            }
            // Create a new block with the right difficulty target given our blistering speed relative to the huge amount
            // of time it's supposed to take (set in the unit test network parameters).
            var b = prev.CreateNextBlock(_coinbaseTo, (uint) Block.FakeClock);
            b.DifficultyTarget = 0x201FFFFF;
            b.Solve();
            Assert.True(_chain.Add(b));
        }

        // Successfully traversed a difficulty transition period.
        [Test]
        public void TestBadDifficulty()
        {
            Assert.True(_testNetChain.Add(GetBlock1()));
            var b2 = GetBlock2();
            Assert.True(_testNetChain.Add(b2));
            var params2 = NetworkParameters.TestNet();
            var bad = new Block(params2);
            // Merkle root can be anything here, doesn't matter.
            bad.MerkleRoot = Hex.Decode("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            // Nonce was just some number that made the hash < difficulty limit set below, it can be anything.
            bad.Nonce = 140548933;
            bad.Time = 1279242649;
            bad.PrevBlockHash = b2.Hash;
            // We're going to make this block so easy 50% of solutions will pass, and check it gets rejected for having a
            // bad difficulty target. Unfortunately the encoding mechanism means we cannot make one that accepts all
            // solutions.
            bad.DifficultyTarget = Block.EasiestDifficultyTarget;
            try
            {
                _testNetChain.Add(bad);
                // The difficulty target above should be rejected on the grounds of being easier than the networks
                // allowable difficulty.
                Assert.Fail();
            }
            catch (VerificationException e)
            {
                Assert.True(e.Message.IndexOf("Difficulty target is bad") >= 0, e.Message);
            }

            // Accept any level of difficulty now.
            params2.ProofOfWorkLimit = new BigInteger("00ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", 16);
            try
            {
                _testNetChain.Add(bad);
                // We should not get here as the difficulty target should not be changing at this point.
                Assert.Fail();
            }
            catch (VerificationException e)
            {
                Assert.True(e.Message.IndexOf("Unexpected change in difficulty") >= 0, e.Message);
            }

            // TODO: Test difficulty change is not out of range when a transition period becomes valid.
        }

        // Some blocks from the test net.
        private static Block GetBlock2()
        {
            var b2 = new Block(_testNet);
            b2.MerkleRoot = Hex.Decode("addc858a17e21e68350f968ccd384d6439b64aafa6c193c8b9dd66320470838b");
            b2.Nonce = 2642058077;
            b2.Time = 1296734343;
            b2.PrevBlockHash = Hex.Decode("000000033cc282bc1fa9dcae7a533263fd7fe66490f550d80076433340831604");
            Assert.AreEqual("000000037b21cac5d30fc6fda2581cf7b2612908aed2abbcc429c45b0557a15f", b2.HashAsString);
            b2.Verify();
            return b2;
        }

        private static Block GetBlock1()
        {
            var b1 = new Block(_testNet);
            b1.MerkleRoot = Hex.Decode("0e8e58ecdacaa7b3c6304a35ae4ffff964816d2b80b62b58558866ce4e648c10");
            b1.Nonce = 236038445;
            b1.Time = 1296734340;
            b1.PrevBlockHash = Hex.Decode("00000007199508e34a9ff81e6ec0c477a4cccff2a4767a8eee39c11db367b008");
            Assert.AreEqual("000000033cc282bc1fa9dcae7a533263fd7fe66490f550d80076433340831604", b1.HashAsString);
            b1.Verify();
            return b1;
        }
    }
}