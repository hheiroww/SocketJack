using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Chat;
using SocketJack.Multiplayer.Transport;
using SocketJack.Multiplayer.Voice;

namespace SocketJack.Multiplayer.Voice.Tests
{
    [TestClass]
    public sealed class VoiceTests
    {
        private static float[] Tone(int frame, int rate = 16000) => Enumerable.Range(0, rate / 50).Select(i => (float)(.3 * Math.Sin(2 * Math.PI * 440 * (frame * (rate / 50) + i) / rate))).ToArray();
        [DataTestMethod]
        [DataRow(8000)] [DataRow(12000)] [DataRow(16000)] [DataRow(24000)] [DataRow(48000)]
        public void ManagedOpusEncodesRealAudioWithBoundedTwentyMillisecondPackets(int rate)
        {
            {
                var options = new VoiceOptions { SampleRate = rate }; var encoder = new OpusVoiceCodec(options); var decoder = new OpusVoiceCodec(options);
                double energy = 0; int bytes = 0;
                for (int frame = 0; frame < 12; frame++)
                {
                    byte[] packet = encoder.Encode(Tone(frame, rate)); Assert.IsTrue(OpusVoiceCodec.IsValidPacket(packet, rate)); Assert.IsTrue(packet.Length <= options.MaxEncodedBytes); bytes += packet.Length;
                    var pcm = new float[options.SamplesPerFrame]; Assert.AreEqual(pcm.Length, decoder.Decode(packet, pcm)); if (frame > 2) energy += pcm.Sum(value => value * value);
                }
                Assert.IsTrue(energy > 1, "Decoded audio energy was " + energy + " at " + rate + " Hz."); Assert.IsTrue(bytes < 12 * options.SamplesPerFrame * 2, "Opus did not compress the " + rate + " Hz input.");
                Assert.ThrowsException<ArgumentException>(() => encoder.Encode(new float[] { float.NaN }));
                Assert.IsFalse(OpusVoiceCodec.IsValidPacket(new byte[401], rate));
            }
        }
        [TestMethod]
        public void JitterWindowReordersDeduplicatesWrapsConcealsAndExpiresWithoutUnboundedBacklog()
        {
            var codec = new OpusVoiceCodec(); var jitter = new VoiceJitterBuffer(); var pcm = new float[320];
            Assert.IsTrue(jitter.Enqueue(uint.MaxValue, codec.Encode(Tone(0)), 0));
            Assert.IsTrue(jitter.Enqueue(1, codec.Encode(Tone(2)), .01)); Assert.IsTrue(jitter.Enqueue(0, codec.Encode(Tone(1)), .02));
            Assert.IsFalse(jitter.Enqueue(0, codec.Encode(Tone(1)), .03)); Assert.AreEqual(3, jitter.BufferedFrames);
            Assert.IsFalse(jitter.TryDecode(pcm, .04)); Assert.IsTrue(jitter.TryDecode(pcm, .061)); Assert.IsTrue(jitter.TryDecode(pcm, .081)); Assert.IsTrue(jitter.TryDecode(pcm, .101));
            Assert.IsTrue(jitter.TryDecode(pcm, .121)); Assert.AreEqual(1L, jitter.ConcealedFrames); Assert.AreEqual(1L, jitter.DuplicateOrLatePackets);
            Assert.IsFalse(jitter.TryDecode(pcm, 3)); Assert.IsTrue(pcm.All(value => value == 0));
            Assert.IsTrue(jitter.Enqueue(1000, codec.Encode(Tone(0)), 4)); Assert.IsTrue(jitter.BufferedFrames <= 10);
        }
        [TestMethod]
        public void AudioPcmRingResamplesInterleavedOutputAndBoundsOverflowAndUnderflow()
        {
            var ring = new VoicePcmBuffer(16000, 3);
            for (int i = 0; i < 5; i++) ring.Write(Tone(i));
            Assert.AreEqual(960, ring.BufferedSamples); Assert.AreEqual(640L, ring.DroppedSamples);
            var stereo = new float[1920]; ring.Read(stereo, 2, 48000);
            Assert.IsTrue(stereo.Sum(value => value * value) > 1);
            for (int i = 0; i < stereo.Length; i += 2) Assert.AreEqual(stereo[i], stereo[i + 1]);
            for (int i = 0; i < 5; i++) ring.Read(stereo, 2, 48000);
            Assert.IsTrue(stereo.All(value => value == 0)); ring.Clear(); Assert.AreEqual(0, ring.BufferedSamples);
        }
        private sealed class Directory : IChatMembershipProvider
        {
            internal readonly Dictionary<string, ChatPresence> Values = new Dictionary<string, ChatPresence>();
            public bool TryGetPresence(string id, out ChatPresence presence) => Values.TryGetValue(id, out presence);
            public IEnumerable<ChatPresence> GetOnlinePresences() => Values.Values.ToArray();
        }
        [TestMethod]
        public void SocialVoiceMembershipUsesTrustedAccountIgnoreRolesAndDoesNotCreateChatMessages()
        {
            string a = Guid.NewGuid().ToString("N"), b = Guid.NewGuid().ToString("N"), c = Guid.NewGuid().ToString("N"); var directory = new Directory();
            foreach (string id in new[] { a, b, c }) directory.Values[id] = new ChatPresence { AccountId = id, DisplayName = id, IsOnline = true, HasGamePresence = true, WorldId = "world" };
            var social = new SocialService(directory); var group = social.CreateGroup(a, SocialGroupKind.Party); social.RespondInvitation(b, social.InviteGroup(a, group.Id, b).Id, true);
            var map = new Dictionary<ulong, string> { [1] = a, [2] = b, [3] = c };
            var provider = new SocialVoiceMembershipProvider(social, directory, peer => map.TryGetValue(peer, out string id) ? id : null, () => map.Keys);
            Assert.IsTrue(provider.TryGetParticipant(1, out var speaker)); CollectionAssert.AreEquivalent(new ulong[] { 1, 2 }, provider.ResolveAudience(speaker, VoiceChannel.Party, group.Id, "").ToArray());
            Assert.AreEqual(0, social.ReadMessages(b).Count); social.SetIgnored(b, a, true);
            CollectionAssert.AreEquivalent(new ulong[] { 1 }, provider.ResolveAudience(speaker, VoiceChannel.Party, group.Id, "").ToArray()); Assert.IsFalse(provider.TryGetParticipant(99, out _));
        }
        [DataTestMethod]
        [DataRow(false)] [DataRow(true)]
        public void RealUdpAndTcpVoiceEnforcesOwnerEpochProximityAndDistinctSpeakerStreams(bool tcp)
        {
            IGameTransport Create() => tcp ? (IGameTransport)new TcpGameTransport() : new UdpGameTransport();
            using var server = new NetworkServer(Create()); using var owner = new NetworkClient(Create()); using var observer = new NetworkClient(Create()); using var far = new NetworkClient(Create());
            server.Start(new IPEndPoint(IPAddress.Loopback, 0)); owner.Connect(server.Transport.LocalEndPoint); observer.Connect(server.Transport.LocalEndPoint); far.Connect(server.Transport.LocalEndPoint);
            NetworkVoiceServer relay = null; var clock = Stopwatch.StartNew();
            void Pump() { relay?.Update(clock.Elapsed.TotalSeconds); server.Update(.002); owner.Update(.002); observer.Update(.002); far.Update(.002); Thread.Sleep(1); }
            void Until(Func<bool> condition) { double end = clock.Elapsed.TotalSeconds + 5; while (!condition() && clock.Elapsed.TotalSeconds < end) Pump(); Assert.IsTrue(condition(), "Loopback condition timed out."); }
            Until(() => owner.IsConnected && observer.IsConnected && far.IsConnected);
            var first = server.Spawn(1, owner.LocalPeerId); var second = server.Spawn(1, observer.LocalPeerId);
            Until(() => owner.Objects.Count == 2 && observer.Objects.Count == 2 && far.Objects.Count == 2);
            var provider = new TrustedGuestVoiceMembershipProvider(() => server.Peers, peer => peer == far.LocalPeerId ? new Vector3(100, 0, 0) : Vector3.Zero);
            using var ownedRelay = new NetworkVoiceServer(server, provider); relay = ownedRelay;
            using var aVoice = new NetworkVoiceClient(owner); using var bVoice = new NetworkVoiceClient(observer); using var cVoice = new NetworkVoiceClient(far);
            int receivedB = 0, receivedC = 0; var speakers = new HashSet<ulong>(); bVoice.FrameReceived += frame => { receivedB++; speakers.Add(frame.PeerId); }; cVoice.FrameReceived += frame => { receivedC++; speakers.Add(frame.PeerId); };
            byte[] captured = null; server.MessageReceived += message => { if (message.MessageId == 60010 && message.PeerId == owner.LocalPeerId) captured = message.Payload; };
            Assert.IsTrue(aVoice.Send(first.Id, Tone(0))); Until(() => receivedB == 1); Assert.AreEqual(0, receivedC);
            Assert.IsFalse(bVoice.Send(first.Id, Tone(1))); Assert.IsNotNull(captured);
            long before = relay.RejectedFrames; Assert.IsTrue(observer.SendMessage(60010, captured, DeliveryMode.Unreliable, 3)); Until(() => relay.RejectedFrames > before); Assert.AreEqual(1, receivedB);
            Assert.IsTrue(aVoice.Send(first.Id, Tone(1), VoiceChannel.Global)); Assert.IsTrue(bVoice.Send(second.Id, Tone(1), VoiceChannel.Global)); Until(() => receivedC == 2 && receivedB == 2); Assert.AreEqual(2, speakers.Count);
            byte[] oldEpoch = (byte[])captured.Clone(); server.TransferOwnership(first.Id, observer.LocalPeerId); Until(() => observer.Objects[first.Id].OwnerId == observer.LocalPeerId);
            before = relay.RejectedFrames; Assert.IsTrue(owner.SendMessage(60010, oldEpoch, DeliveryMode.Unreliable, 3)); Until(() => relay.RejectedFrames > before);
            Assert.IsTrue(bVoice.Send(first.Id, Tone(2), VoiceChannel.Global)); Until(() => receivedC == 3);
            before = relay.RejectedFrames; Assert.IsTrue(observer.SendMessage(60010, new byte[10], DeliveryMode.Unreliable, 3)); Until(() => relay.RejectedFrames > before);
            Assert.IsTrue(owner.IsConnected && observer.IsConnected && far.IsConnected);
        }
    }
}
