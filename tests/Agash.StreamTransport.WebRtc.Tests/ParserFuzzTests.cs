using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;
using Agash.StreamTransport.WebRtc.Sdp;
using Agash.StreamTransport.WebRtc.Srtp;
using Agash.StreamTransport.WebRtc.Stun;
using Agash.StreamTransport.WebRtc.Turn;
using Dtls.Core;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// Feeds every parser that reads what a peer or the network sends with mutations of valid input (bit
/// flips, truncations, extensions, splices, random bytes) and checks that each one rejects or parses without
/// throwing and in bounded time. The seed is fixed so a failure reproduces; the input that failed is printed.
/// </summary>
[TestClass]
public sealed class ParserFuzzTests
{
    // STREAMTRANSPORT_FUZZ_ITERATIONS raises it for a soak run.
    private static readonly int Iterations = int.TryParse(
        Environment.GetEnvironmentVariable("STREAMTRANSPORT_FUZZ_ITERATIONS"),
        out int iterations
    )
        ? iterations
        : 20_000;

    [TestMethod]
    public void Stun_MutatedMessages_NeverThrow()
    {
        byte[][] seeds = [StunRequest(), StunDataIndication(), StunError()];
        Fuzz(
            seeds,
            static input =>
            {
                if (StunMessageReader.TryParse(input, out StunMessageReader message))
                {
                    _ = message.TryGetXorMappedAddress(out _);
                    _ = message.TryGetXorAddress(StunAttributeType.XorPeerAddress, out _);
                    _ = message.TryGetXorAddress(StunAttributeType.XorRelayedAddress, out _);
                    _ = message.TryGetErrorCode(out _);
                    _ = message.TryGetUInt32(StunAttributeType.Lifetime, out _);
                    _ = message.TryFindAttribute(StunAttributeType.Data, out _);
                    _ = message.VerifyFingerprint();
                    _ = message.VerifyMessageIntegrity("password"u8);
                }
            }
        );
    }

    [TestMethod]
    public void Rtp_MutatedPackets_NeverThrow()
    {
        byte[][] seeds = [Rtp(withExtension: false), Rtp(withExtension: true)];
        Fuzz(seeds, static input => _ = RtpPacket.TryParse(input, out _, out _));
    }

    [TestMethod]
    public void Rtcp_MutatedCompounds_NeverThrow()
    {
        byte[][] seeds = [Nack(), Pli(), Ccfb(), [.. Pli(), .. Nack()]];
        List<ushort> lost = [];
        List<CcfbStreamReport> streams = [];
        Fuzz(
            seeds,
            input =>
            {
                foreach (RtcpElement element in RtcpCompound.Enumerate(input))
                {
                    _ = element.Body.Length;
                }

                _ = RtcpSenderReport.TryParse(input, out _);
                _ = RtcpReceiverReport.TryParse(input, out _);
                lost.Clear();
                _ = RtcpFeedback.TryParseNack(input, out _, lost);
                _ = RtcpFeedback.ContainsPli(input, out _);
                streams.Clear();
                _ = Agash.StreamTransport.WebRtc.Rtcp.Ccfb.TryParse(input, out _, out _, streams);
            }
        );
    }

    [TestMethod]
    public void Srtp_MutatedProtectedPackets_AreRejectedWithoutThrowing()
    {
        foreach (SrtpProtectionProfile profile in SrtpSession.Profiles)
        {
            int key = profile == SrtpProtectionProfile.AeadAes256Gcm ? 32 : 16;
            int salt = profile == SrtpProtectionProfile.Aes128CmHmacSha180 ? 14 : 12;
            var keying = new SrtpKeyingMaterial(
                profile,
                new byte[key],
                new byte[salt],
                new byte[key],
                new byte[salt]
            );
            using var sender = new SrtpSession(keying, true);
            using var receiver = new SrtpSession(keying, false);
            byte[] packet = new byte[512];
            byte[] rtp = Rtp(withExtension: true);
            rtp.CopyTo(packet, 0);
            byte[] protectedRtp = packet[..sender.ProtectRtp(packet, rtp.Length)];
            byte[] rtcp = new byte[512];
            byte[] pli = Pli();
            pli.CopyTo(rtcp, 0);
            byte[] protectedRtcp = rtcp[..sender.ProtectRtcp(rtcp, pli.Length)];
            Fuzz(
                [protectedRtp, protectedRtcp],
                input =>
                {
                    _ = receiver.UnprotectRtp(input, input.Length, out _);
                    _ = receiver.UnprotectRtcp(input, input.Length, out _);
                },
                Iterations / 4
            );
        }
    }

    [TestMethod]
    public void Sdp_MutatedDescriptions_NeverThrow()
    {
        string offer = """
            v=0
            o=- 4611731400430051336 2 IN IP4 127.0.0.1
            s=-
            t=0 0
            a=group:BUNDLE 0 1
            a=fingerprint:sha-256 8F:2A:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD
            m=audio 9 UDP/TLS/RTP/SAVPF 111
            c=IN IP4 0.0.0.0
            a=mid:0
            a=ice-ufrag:abcd
            a=ice-pwd:aaaaaaaaaaaaaaaaaaaaaaaa
            a=setup:actpass
            a=rtcp-mux
            a=rtpmap:111 opus/48000/2
            a=fmtp:111 minptime=10;useinbandfec=1
            a=ssrc:1234 cname:x
            m=video 9 UDP/TLS/RTP/SAVPF 96 97
            c=IN IP4 0.0.0.0
            a=mid:1
            a=rtpmap:96 H264/90000
            a=fmtp:96 profile-level-id=42e01f;packetization-mode=1;x-alpha=layer,side-by-side
            a=rtpmap:97 rtx/90000
            a=fmtp:97 apt=96
            a=rtcp-fb:96 nack pli
            a=extmap:1 http://www.webrtc.org/experiments/rtp-hdrext/abs-capture-time
            a=ssrc-group:FID 5678 9012
            a=candidate:1 1 udp 2130706431 2001:db8::1 50000 typ host
            """;
        byte[] seed = Encoding.UTF8.GetBytes(offer.ReplaceLineEndings("\r\n"));
        Fuzz(
            [seed],
            static input =>
            {
                string text = Encoding.UTF8.GetString(input);
                if (SdpReader.TryParse(text, out SdpDescription description))
                {
                    _ = SdpWriter.Write(description);
                }

                foreach (string line in text.Split('\n'))
                {
                    _ = IceCandidate.TryParse(line, out _);
                    _ = TurnServer.TryParse(line, "u", "p", out _);
                }
            },
            Iterations / 4
        );
    }

    [TestMethod]
    [DataRow("H264")]
    [DataRow("H265")]
    [DataRow("AV1")]
    [DataRow("opus")]
    public void Depacketizer_MutatedPayloads_NeverThrow(string codec)
    {
        RtpPayloadFormat format = codec switch
        {
            "H264" => H264PayloadFormat.Instance,
            "H265" => H265PayloadFormat.Instance,
            "AV1" => Av1PayloadFormat.Instance,
            _ => OpusPayloadFormat.Instance,
        };
        byte[] frame = codec switch
        {
            "H264" => AnnexB(
                [0x67, 0x42, 0xE0, 0x1F],
                [0x68, 0xCE, 0x3C, 0x80],
                [0x65, .. Bytes(3000)]
            ),
            "H265" => AnnexB(
                [0x40, 0x01, 0x0C],
                [0x42, 0x01, 0x01],
                [0x44, 0x01, 0xC0],
                [0x26, 0x01, .. Bytes(3000)]
            ),
            "AV1" =>
            [
                0x12,
                0x00,
                0x0A,
                0x0B,
                0x00,
                0x00,
                0x00,
                0x24,
                0xCF,
                0x7F,
                0x0D,
                0xBF,
                0xFF,
                0x30,
                0x32,
                0xA0,
                .. Bytes(10),
            ],
            _ => Bytes(200),
        };
        RtpPayloadWriter writer = new();
        format.CreatePacketizer().Packetize(frame, 1100, writer);
        byte[][] seeds = [.. Enumerable.Range(0, writer.Count).Select(i => writer[i].ToArray())];
        using IRtpDepacketizer depacketizer = format.CreateDepacketizer();
        using var buffer = new RtpFrameBuffer(format);
        var random = new Random(7);
        ushort sequence = 0;
        Fuzz(
            seeds,
            input =>
            {
                if (
                    depacketizer.TryPush(
                        input,
                        random.Next(4) == 0,
                        out EncodedFrameBuffer assembled
                    )
                )
                {
                    assembled.Dispose();
                }

                RtpFrameBuffer.InsertResult result = buffer.Insert(
                    (ushort)(sequence + random.Next(-3, 5)),
                    (uint)(random.Next(3) * 3000),
                    random.Next(3) == 0,
                    input
                );
                sequence++;
                foreach (RtpFrameBuffer.AssembledFrame done in result.Frames)
                {
                    done.Frame.Dispose();
                }
            }
        );
        if (format == Av1PayloadFormat.Instance)
        {
            var obus = new Obu[64];
            // The sender parses its own encoder's output; malformed units are refused as invalid data.
            Fuzz(
                [frame],
                input =>
                {
                    try
                    {
                        _ = Obu.Parse(
                            input,
                            obus.AsSpan(0, Math.Min(obus.Length, Obu.Count(input)))
                        ).Length;
                    }
                    catch (InvalidDataException)
                    {
                        // The designed refusal.
                    }
                }
            );
        }
    }

    // Runs a target over mutations of the seeds; a throw or a slow call fails with the input.
    private static void Fuzz(byte[][] seeds, Action<byte[]> target, int? iterations = null)
    {
        var random = new Random(20261001);
        foreach (byte[] seed in seeds)
        {
            Run(target, seed);
        }

        for (int i = 0; i < (iterations ?? Iterations); i++)
        {
            byte[] input = Mutate(
                seeds[random.Next(seeds.Length)],
                seeds[random.Next(seeds.Length)],
                random
            );
            Run(target, input);
        }
    }

    private static void Run(Action<byte[]> target, byte[] input)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            target(input);
        }
        catch (Exception exception)
        {
            Assert.Fail($"{exception} for input {Convert.ToHexString(input)}");
        }

        Assert.IsLessThan(
            1000,
            Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            $"slow input {Convert.ToHexString(input)}"
        );
    }

    private static byte[] Mutate(byte[] seed, byte[] other, Random random)
    {
        List<byte> bytes = [.. seed];
        int edits = 1 + random.Next(6);
        for (int e = 0; e < edits; e++)
        {
            switch (random.Next(8))
            {
                case 0 when bytes.Count > 0:
                    bytes[random.Next(bytes.Count)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1 when bytes.Count > 0:
                    bytes[random.Next(bytes.Count)] = (byte)random.Next(256);
                    break;
                case 2 when bytes.Count > 0:
                    bytes.RemoveRange(random.Next(bytes.Count), 1);
                    break;
                case 3:
                    bytes.Insert(random.Next(bytes.Count + 1), (byte)random.Next(256));
                    break;
                case 4 when bytes.Count > 1:
                    bytes.RemoveRange(random.Next(bytes.Count), 0);
                    bytes = bytes[..random.Next(bytes.Count)];
                    break;
                case 5:
                    int at = random.Next(other.Length + 1);
                    bytes.AddRange(other[at..]);
                    break;
                case 6 when bytes.Count >= 2:
                    // Interesting values in a length-like field.
                    int i = random.Next(bytes.Count - 1);
                    byte[] values = [0x00, 0x01, 0x7F, 0x80, 0xFF];
                    bytes[i] = values[random.Next(values.Length)];
                    bytes[i + 1] = values[random.Next(values.Length)];
                    break;
                default:
                    bytes.Add((byte)random.Next(256));
                    break;
            }
        }

        return [.. bytes];
    }

    private static byte[] StunRequest()
    {
        byte[] buffer = new byte[256];
        StunMessageWriter writer = new(
            buffer,
            StunMessageClass.Request,
            StunMethod.Binding,
            RandomNumberGenerator.GetBytes(12)
        );
        writer.AddAttribute(StunAttributeType.Username, "abcd:efgh"u8);
        writer.AddXorMappedAddress(new IPEndPoint(IPAddress.Parse("2001:db8::7"), 4000));
        writer.AddAttribute(StunAttributeType.UseCandidate, default);
        writer.AddMessageIntegrity("password"u8);
        writer.AddFingerprint();
        return buffer[..writer.Length];
    }

    private static byte[] StunDataIndication()
    {
        byte[] buffer = new byte[256];
        StunMessageWriter writer = new(
            buffer,
            StunMessageClass.Indication,
            StunMethod.Data,
            RandomNumberGenerator.GetBytes(12)
        );
        writer.AddXorAddress(
            StunAttributeType.XorPeerAddress,
            new IPEndPoint(IPAddress.Parse("192.0.2.1"), 5000)
        );
        writer.AddAttribute(StunAttributeType.Data, Bytes(40));
        return buffer[..writer.Length];
    }

    private static byte[] StunError()
    {
        byte[] buffer = new byte[256];
        StunMessageWriter writer = new(
            buffer,
            StunMessageClass.ErrorResponse,
            StunMethod.Allocate,
            RandomNumberGenerator.GetBytes(12)
        );
        writer.AddErrorCode(401, "Unauthorized");
        writer.AddAttribute(StunAttributeType.Realm, "realm"u8);
        writer.AddAttribute(StunAttributeType.Nonce, "nonce"u8);
        writer.AddUInt32(StunAttributeType.Lifetime, 600);
        return buffer[..writer.Length];
    }

    private static byte[] Rtp(bool withExtension)
    {
        byte[] buffer = new byte[256];
        int length = RtpPacket.Write(
            buffer,
            true,
            96,
            1000,
            90000,
            0x11223344,
            Bytes(60),
            withExtension ? RtpExtensionMap.Offered : null,
            new RtpExtensionValues(
                0x0123456789ABCDEFUL,
                new VideoTiming(VideoTiming.ByTimer, 1, 2, 3, 4, 0, 0),
                new PlayoutDelay(TimeSpan.Zero, TimeSpan.FromMilliseconds(100))
            )
        );
        return buffer[..length];
    }

    private static byte[] Nack()
    {
        byte[] buffer = new byte[128];
        return buffer[..RtcpFeedback.BuildNack(buffer, 1, 2, [100, 101, 105, 140])];
    }

    private static byte[] Pli()
    {
        byte[] buffer = new byte[64];
        return buffer[..RtcpFeedback.BuildPli(buffer, 1, 2)];
    }

    private static byte[] Ccfb()
    {
        byte[] buffer = new byte[256];
        CcfbStreamReport report = new(
            2,
            100,
            [new CcfbMetric(true, 0, 10), new CcfbMetric(false, 0, 0), new CcfbMetric(true, 1, 20)]
        );
        return buffer[..Agash.StreamTransport.WebRtc.Rtcp.Ccfb.Build(buffer, 1, [report], 1234)];
    }

    private static byte[] AnnexB(params byte[][] units) =>
        [.. units.SelectMany(static u => (byte[])[0, 0, 0, 1, .. u])];

    private static byte[] Bytes(int count)
    {
        byte[] bytes = new byte[count];
        new Random(count).NextBytes(bytes);
        return bytes;
    }
}

[TestClass]
public sealed class IceCandidateParseTests
{
    [TestMethod]
    [DataRow("candidate:1 1 udp 2130706431 192.0.2.1 99999 typ host")]
    [DataRow("candidate:1 1 udp 2130706431 192.0.2.1 -1 typ host")]
    [DataRow("candidate:1 1 udp 1694498815 192.0.2.1 5000 typ srflx raddr 10.0.0.1 rport 70000")]
    public void TryParse_PortOutOfRange_IsRejected(string candidate) =>
        Assert.IsFalse(IceCandidate.TryParse(candidate, out _));
}
