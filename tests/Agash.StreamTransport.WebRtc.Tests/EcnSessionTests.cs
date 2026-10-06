using System.Collections.Concurrent;
using System.Net;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

// ECN for RTP end to end (RFC 6679, RFC 8888 section 7): negotiated, validated on the path, and carried only
// by media.
[TestClass]
public sealed class EcnSessionTests
{
    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();
    private static readonly SdpCodec H264 = new(
        96,
        "H264",
        90000,
        null,
        "packetization-mode=1;profile-level-id=42e01f",
        ["nack", "nack pli"]
    );

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void EcnCapableRtp_IsWrittenAndRead()
    {
        SdpCodec[] codecs = [H264];
        SdpDescription description = new()
        {
            Media =
            [
                new SdpMediaDescription
                {
                    Kind = SdpMediaKind.Video,
                    Mid = "0",
                    Codecs = codecs,
                    IceUfrag = "ufrag",
                    IcePwd = "passwordpasswordpassword",
                    Fingerprint = Certificate.Fingerprint,
                    Ecn = new SdpEcnCapability(["rtp", "ice"], "setonly", "1"),
                },
            ],
        };

        string text = SdpWriter.Write(description);
        StringAssert.Contains(text, "a=ecn-capable-rtp: rtp,ice mode=setonly; ect=1");
        Assert.IsTrue(SdpReader.TryParse(text, out SdpDescription read));
        SdpEcnCapability ecn = read.Media[0].Ecn!;
        CollectionAssert.AreEqual(new[] { "rtp", "ice" }, ecn.Methods.ToArray());
        Assert.IsTrue(ecn.CanSet);
        Assert.IsFalse(ecn.CanRead);
        Assert.AreEqual("1", ecn.Ect);
    }

    // The path carries ECN: validation succeeds, media is marked ECT(1), and nothing else ever is.
    [TestMethod]
    [Timeout(60_000)]
    public async Task CleanPath_ValidatesAndMarksOnlyMedia()
    {
        Datagrams seen = new();
        await using Session session = await Session.ConnectAsync(seen.Record);

        await session.SendUntilAsync(
            () => session.Sender.EcnState == EcnState.Capable,
            TestContext.CancellationToken
        );

        Assert.AreEqual(EcnState.Capable, session.Sender.EcnState);
        Assert.Contains(EcnCodepoint.Ect1, seen.Of(Kind.Rtp));
        Assert.IsTrue(
            seen.Of(Kind.Rtcp).All(static e => e == EcnCodepoint.NotEct),
            "RTCP is never ECT"
        );
        Assert.IsTrue(
            seen.Of(Kind.Stun).All(static e => e == EcnCodepoint.NotEct),
            "STUN is never ECT"
        );
        Assert.IsTrue(
            seen.Of(Kind.Dtls).All(static e => e == EcnCodepoint.NotEct),
            "DTLS is never ECT"
        );
    }

    // A middlebox that clears the ECN field: the validator sees marks arrive unmarked and stops marking.
    [TestMethod]
    [Timeout(60_000)]
    public async Task BleachingPath_FailsValidationAndStopsMarking()
    {
        await using Session session = await Session.ConnectAsync(
            static (_, _) => EcnCodepoint.NotEct
        );

        await session.SendUntilAsync(
            () => session.Sender.EcnState == EcnState.Failed,
            TestContext.CancellationToken
        );

        Assert.AreEqual(EcnState.Failed, session.Sender.EcnState);
    }

    // A peer that did not ask for ECT(1) gets ECT(0), and the controller answers CE classically.
    [TestMethod]
    [Timeout(60_000)]
    public async Task PeerWithoutEct1Preference_GetsEct0()
    {
        Datagrams seen = new();
        await using Session session = await Session.ConnectAsync(seen.Record, answerEct: null);

        await session.SendUntilAsync(
            () => seen.Of(Kind.Rtp).Contains(EcnCodepoint.Ect0),
            TestContext.CancellationToken
        );

        Assert.Contains(EcnCodepoint.Ect0, seen.Of(Kind.Rtp));
        Assert.DoesNotContain(EcnCodepoint.Ect1, seen.Of(Kind.Rtp));
    }

    private enum Kind
    {
        Stun,
        Dtls,
        Rtp,
        Rtcp,
    }

    // Every datagram's kind (RFC 7983 demultiplexing, RFC 5761 for RTCP) and the codepoint it was sent with.
    private sealed class Datagrams
    {
        private readonly ConcurrentQueue<(Kind Kind, EcnCodepoint Ecn)> _seen = new();

        public EcnCodepoint Record(byte[] data, EcnCodepoint ecn)
        {
            Kind? kind = data[0] switch
            {
                <= 3 => Kind.Stun,
                >= 20 and <= 63 => Kind.Dtls,
                >= 128 and <= 191 when data.Length > 1 && data[1] is >= 192 and <= 223 => Kind.Rtcp,
                >= 128 and <= 191 => Kind.Rtp,
                _ => null,
            };
            if (kind is { } k)
            {
                _seen.Enqueue((k, ecn));
            }

            return ecn;
        }

        public EcnCodepoint[] Of(Kind kind) =>
            [.. _seen.Where(s => s.Kind == kind).Select(static s => s.Ecn)];
    }

    // A sender with a congestion controller and a receiver, on the in-memory network.
    private sealed class Session : IAsyncDisposable
    {
        private Session(PeerConnection sender, PeerConnection receiver)
        {
            Sender = sender;
            Receiver = receiver;
        }

        public PeerConnection Sender { get; }

        public PeerConnection Receiver { get; }

        public static async Task<Session> ConnectAsync(
            Func<byte[], EcnCodepoint, EcnCodepoint> path,
            string? answerEct = "1"
        )
        {
            InMemoryIceNetwork network = new() { Remark = path };
            PeerConnection sender = new(
                new PeerConnectionOptions
                {
                    Media = [new MediaLine("0", SdpMediaKind.Video, 0xAAAA_0001, [H264])],
                    SocketFactory = network.Factory(IPAddress.Parse("10.0.0.1")),
                },
                Certificate,
                controller: new ScreamCongestionController()
            );
            PeerConnection receiver = new(
                new PeerConnectionOptions
                {
                    Media = [new MediaLine("0", SdpMediaKind.Video, 0xBBBB_0001, [H264])],
                    SocketFactory = network.Factory(IPAddress.Parse("10.0.0.2")),
                },
                Certificate,
                controller: new ScreamCongestionController()
            );
            sender.LocalIceCandidate += receiver.AddRemoteIceCandidate;
            receiver.LocalIceCandidate += sender.AddRemoteIceCandidate;
            TaskCompletionSource connected = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            int up = 0;
            void OnState(PeerConnectionState state)
            {
                if (state == PeerConnectionState.Connected && Interlocked.Increment(ref up) == 2)
                {
                    connected.TrySetResult();
                }
            }

            sender.StateChanged += OnState;
            receiver.StateChanged += OnState;
            receiver.SetRemoteDescription(sender.CreateOffer(), SdpType.Offer);
            SdpDescription answer = receiver.CreateAnswer();
            if (answerEct is null)
            {
                Assert.IsTrue(
                    SdpReader.TryParse(SdpWriter.Write(answer).Replace("; ect=1", ""), out answer)
                );
            }

            sender.SetRemoteDescription(answer, SdpType.Answer);
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return new Session(sender, receiver);
        }

        // Sends video at a frame rate until the condition holds or the guard runs out.
        public async Task SendUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
        {
            using PeriodicTimer frames = new(TimeSpan.FromMilliseconds(10));
            using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            guard.CancelAfter(TimeSpan.FromSeconds(20));
            uint timestamp = 0;
            try
            {
                while (!condition() && await frames.WaitForNextTickAsync(guard.Token))
                {
                    _ = Sender.TrySendRtp(96, 0xAAAA_0001, timestamp += 900, true, [0x65, 1, 2, 3]);
                }
            }
            catch (OperationCanceledException)
            {
                // The guard ran out; the caller asserts on the condition.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Sender.DisposeAsync();
            await Receiver.DisposeAsync();
        }
    }
}
