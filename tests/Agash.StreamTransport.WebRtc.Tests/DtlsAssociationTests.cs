using System.Net;
using System.Security.Cryptography;
using Agash.StreamTransport.WebRtc.Sdp;
using Dtls.Core;

namespace Agash.StreamTransport.WebRtc.Tests;

// The DTLS association as SDP negotiates it: RFC 8842 (tls-id, setup, a new association only through
// a new offer/answer), RFC 4145 section 4.1 (an answer is active or passive) and RFC 8122 section 5.1
// (several fingerprints, of several hash functions).
[TestClass]
public sealed class DtlsAssociationTests
{
    private const string OfferTlsId = "abc3de65cddef001be82aa";

    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    private static readonly SdpCodec Opus = new(111, "opus", 48000, 2, null, []);

    [TestMethod]
    public async Task Offer_CarriesTlsId_OnTheTransportSectionOnly()
    {
        await using PeerConnection offerer = Peer("10.0.0.1");

        SdpDescription offer = offerer.CreateOffer();

        string? tlsId = offer.Media[0].TlsId;
        Assert.IsNotNull(tlsId);
        Assert.IsTrue(SdpReader.IsTlsId(tlsId), "20 to 255 characters of the attribute's alphabet");
        Assert.IsGreaterThanOrEqualTo(24, tlsId.Length, "at least 120 random bits");
        Assert.IsTrue(offer.Media.Skip(1).All(static m => m.TlsId is null));
        StringAssert.Contains(SdpWriter.Write(offer), $"a=tls-id:{tlsId}\r\n");
    }

    [TestMethod]
    public async Task Answer_CarriesTlsId_OnlyWhenTheOfferDid()
    {
        await using PeerConnection with = Peer("10.0.0.2");
        with.SetRemoteDescription(Parse(Offer(tlsId: OfferTlsId)), SdpType.Offer);
        Assert.IsNotNull(with.CreateAnswer().Media[0].TlsId);

        await using PeerConnection without = Peer("10.0.0.3");
        without.SetRemoteDescription(Parse(Offer(tlsId: null)), SdpType.Offer);
        Assert.IsTrue(without.CreateAnswer().Media.All(static m => m.TlsId is null));
    }

    [TestMethod]
    public async Task SetRemoteDescription_ActPassAnswer_IsRefused()
    {
        await using PeerConnection offerer = Peer("10.0.0.1");
        _ = offerer.CreateOffer();

        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            offerer.SetRemoteDescription(Parse(Offer(tlsId: OfferTlsId)), SdpType.Answer)
        );
    }

    // RFC 8842 section 5.3: once roles are agreed, an actpass re-offer is answered without changing them.
    [TestMethod]
    public async Task Answer_ToAnActPassReOffer_KeepsTheAgreedRole()
    {
        await using PeerConnection answerer = Peer("10.0.0.2");
        answerer.SetRemoteDescription(
            Parse(Offer(tlsId: OfferTlsId, setup: "active")),
            SdpType.Offer
        );
        Assert.AreEqual(SdpSetup.Passive, answerer.CreateAnswer().Media[0].Setup);

        answerer.SetRemoteDescription(Parse(Offer(tlsId: OfferTlsId)), SdpType.Offer);

        SdpDescription answer = answerer.CreateAnswer();
        Assert.AreEqual(SdpSetup.Passive, answer.Media[0].Setup);
        Assert.IsFalse(answer.Media[0].Rejected);
    }

    // RFC 8842 sections 3.1 and 5.3: a re-offer with another tls-id, other fingerprints or other roles asks
    // for a new association; an answerer that does not make one rejects the sections.
    [TestMethod]
    [DataRow("tls-id")]
    [DataRow("fingerprint")]
    [DataRow("role")]
    public async Task Answer_ToAReOfferAskingForANewAssociation_RejectsEverySection(string change)
    {
        await using PeerConnection answerer = Peer("10.0.0.2");
        answerer.SetRemoteDescription(Parse(Offer(tlsId: OfferTlsId)), SdpType.Offer);
        Assert.IsFalse(answerer.CreateAnswer().Media[0].Rejected);

        using var other = RtcCertificate.Generate();
        string reOffer = change switch
        {
            "tls-id" => Offer(tlsId: "zyx3de65cddef001be82bb"),
            "fingerprint" => Offer(tlsId: OfferTlsId, certificate: other),
            _ => Offer(tlsId: OfferTlsId, setup: "active"),
        };
        answerer.SetRemoteDescription(Parse(reOffer), SdpType.Offer);

        Assert.IsTrue(answerer.CreateAnswer().Media.All(static m => m.Rejected));
    }

    // RFC 8842 section 5.4: an answer asking for a new association this connection does not make ends it.
    [TestMethod]
    public async Task SetRemoteDescription_ReAnswerWithOtherFingerprints_FailsTheConnection()
    {
        await using PeerConnection offerer = Peer("10.0.0.1");
        _ = offerer.CreateOffer();
        offerer.SetRemoteDescription(
            Parse(Offer(tlsId: OfferTlsId, setup: "active")),
            SdpType.Answer
        );
        Assert.AreNotEqual(PeerConnectionState.Failed, offerer.State);

        using var other = RtcCertificate.Generate();
        offerer.SetRemoteDescription(
            Parse(Offer(tlsId: OfferTlsId, setup: "active", certificate: other)),
            SdpType.Answer
        );

        Assert.AreEqual(PeerConnectionState.Failed, offerer.State);
    }

    [TestMethod]
    [DataRow("a=setup:holdconn", "RFC 8842 section 5.1: never for DTLS")]
    [DataRow("a=setup:bogus", "not an RFC 4145 value")]
    [DataRow("a=tls-id:short", "under 20 characters")]
    [DataRow("a=tls-id:abc3de65cddef001be82!!", "outside the alphabet")]
    public void Reader_InvalidDtlsAttributes_AreRefused(string line, string why)
    {
        string sdp = Offer(tlsId: null).Replace("a=setup:actpass", line, StringComparison.Ordinal);

        Assert.IsFalse(SdpReader.TryParse(sdp, out _), why);
    }

    // RFC 8122 sections 5 and 5.1: every fingerprint is kept, of every hash function it defines except
    // MD5 and MD2, which are left out; with none left the description is unusable.
    [TestMethod]
    public void Reader_SeveralFingerprints_KeepsEveryUsableOne()
    {
        var sha512 = DtlsFingerprint.Compute(Certificate.Certificate, HashAlgorithmName.SHA512);
        string sdp = Offer(tlsId: null)
            .Replace(
                $"a=fingerprint:{Certificate.Fingerprint}",
                $"a=fingerprint:{Certificate.Fingerprint}\r\na=fingerprint:{sha512}\r\na=fingerprint:md5 AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89",
                StringComparison.Ordinal
            );

        Assert.IsTrue(SdpReader.TryParse(sdp, out SdpDescription description));
        CollectionAssert.AreEquivalent(
            new[] { Certificate.Fingerprint, sha512 },
            description.Media[0].Fingerprints.ToArray()
        );
        StringAssert.Contains(SdpWriter.Write(description), $"a=fingerprint:{sha512}\r\n");

        string md5Only = Offer(tlsId: null)
            .Replace(
                $"a=fingerprint:{Certificate.Fingerprint}",
                "a=fingerprint:md5 AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89",
                StringComparison.Ordinal
            );
        Assert.IsFalse(SdpReader.TryParse(md5Only, out _));
    }

    private static PeerConnection Peer(string address) =>
        new(
            new PeerConnectionOptions
            {
                Media = [new MediaLine("0", SdpMediaKind.Audio, 0xA1, [Opus])],
                SocketFactory = new InMemoryIceNetwork().Factory(IPAddress.Parse(address)),
            },
            Certificate
        );

    private static SdpDescription Parse(string sdp)
    {
        Assert.IsTrue(SdpReader.TryParse(sdp, out SdpDescription description), sdp);
        return description;
    }

    private static string Offer(
        string? tlsId,
        string setup = "actpass",
        RtcCertificate? certificate = null
    ) =>
        string.Join(
            "\r\n",
            new[]
            {
                "v=0",
                "o=- 4611731400430051336 2 IN IP4 127.0.0.1",
                "s=-",
                "t=0 0",
                "a=group:BUNDLE 0",
                "m=audio 9 UDP/TLS/RTP/SAVPF 111",
                "c=IN IP4 0.0.0.0",
                "a=ice-ufrag:abcd",
                "a=ice-pwd:0123456789abcdef0123456789",
                $"a=fingerprint:{(certificate ?? Certificate).Fingerprint}",
                $"a=setup:{setup}",
                tlsId is null ? null : $"a=tls-id:{tlsId}",
                "a=mid:0",
                "a=sendrecv",
                "a=rtcp-mux",
                "a=rtpmap:111 opus/48000/2",
                "",
            }.Where(static l => l is not null)
        );
}
