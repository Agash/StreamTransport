using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Agash.StreamTransport.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Agash.StreamTransport.Tests;

// WHEP sections 4.3.2 and 4.3.3: a 406 carrying an SDP offer is the server's counter-offer, answered in
// a PATCH to the resource it names; any other 406 is an ordinary refusal.
[TestClass]
public sealed class WhepCounterOfferTests
{
    private const string Offer =
        "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"
        + "m=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\na=setup:actpass\r\n"
        + "a=ice-ufrag:Cli1\r\na=ice-pwd:ClientPasswordClientPassw\r\n";

    private const string CounterOffer =
        "v=0\r\no=- 7 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"
        + "a=fingerprint:sha-256 00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF\r\n"
        + "m=video 9 UDP/TLS/RTP/SAVPF 102\r\na=mid:v\r\na=setup:actpass\r\n"
        + "a=ice-ufrag:Srv1\r\na=ice-pwd:ServerPasswordServerPassw\r\n";

    private const string Answer =
        "v=0\r\no=- 1 3 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"
        + "m=video 9 UDP/TLS/RTP/SAVPF 102\r\na=mid:v\r\na=setup:active\r\n"
        + "a=ice-ufrag:Cli1\r\na=ice-pwd:ClientPasswordClientPassw\r\n";

    private const string CounterOfferWithoutFingerprint =
        "v=0\r\no=- 7 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"
        + "m=video 9 UDP/TLS/RTP/SAVPF 102\r\na=mid:v\r\na=setup:actpass\r\n"
        + "a=ice-ufrag:Srv1\r\na=ice-pwd:ServerPasswordServerPassw\r\n";

    // An answer, setup active: not an offer.
    private const string CounterAnswer =
        "v=0\r\no=- 7 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"
        + "a=fingerprint:sha-256 00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF\r\n"
        + "m=video 9 UDP/TLS/RTP/SAVPF 102\r\na=mid:v\r\na=setup:active\r\n"
        + "a=ice-ufrag:Srv1\r\na=ice-pwd:ServerPasswordServerPassw\r\n";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CounterOffer_IsAnsweredInAPatchToTheResource()
    {
        CounterOfferingHandler server = new();
        await using HttpSdpChannel channel = Channel(server, TimeProvider.System);
        SessionDescription? offered = null;
        channel.DescriptionReceived += description =>
        {
            offered = description;
            return Task.CompletedTask;
        };

        await channel.SendDescriptionAsync(
            new SessionDescription(SdpKind.Offer, Offer),
            TestContext.CancellationToken
        );

        Assert.AreEqual(new SessionDescription(SdpKind.Offer, CounterOffer), offered);
        Assert.AreEqual(new Uri("https://whep.example/whep/live/r1"), channel.Resource);
        Assert.IsFalse(channel.Answered.IsCompleted, "the session waits for this side's answer");

        await channel.SendDescriptionAsync(
            new SessionDescription(SdpKind.Answer, Answer),
            TestContext.CancellationToken
        );

        Assert.AreEqual(HttpMethod.Patch, server.AnswerMethod);
        Assert.AreEqual(new Uri("https://whep.example/whep/live/r1"), server.AnswerUri);
        Assert.AreEqual("application/sdp", server.AnswerType);
        Assert.AreEqual(Answer, server.AnswerBody);
        Assert.IsTrue(channel.Answered.IsCompletedSuccessfully);
    }

    // Section 4.3.3: without an application/sdp body that is a valid offer, a 406 is not a
    // counter-offer: not SDP, SDP that breaks RFC 8866's syntax, or a section without what a JSEP offer
    // carries (here the fingerprint, and setup actpass).
    [TestMethod]
    [DataRow("no", "text/plain")]
    [DataRow("v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 102\r\n", "application/sdp")]
    [DataRow(CounterOfferWithoutFingerprint, "application/sdp")]
    [DataRow(CounterAnswer, "application/sdp")]
    public async Task NotAcceptableWithoutAnOffer_IsARefusal(string body, string type)
    {
        CounterOfferingHandler server = new(body: body, type: type);
        await using HttpSdpChannel channel = Channel(server, TimeProvider.System);

        HttpRequestException refused = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            channel.SendDescriptionAsync(
                new SessionDescription(SdpKind.Offer, Offer),
                TestContext.CancellationToken
            )
        );

        Assert.AreEqual(HttpStatusCode.NotAcceptable, refused.StatusCode);
        Assert.IsNull(server.AnswerMethod);
    }

    // Section 4.3.2: the counter-offer is valid for 30 seconds unless valid-until says otherwise.
    [TestMethod]
    [DataRow(null, 31)]
    [DataRow(10, 11)]
    public async Task ExpiredCounterOffer_IsNotAnswered(int? validSeconds, int waitSeconds)
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        CounterOfferingHandler server = new(
            validUntil: validSeconds is { } seconds ? time.GetUtcNow().AddSeconds(seconds) : null
        );
        await using HttpSdpChannel channel = Channel(server, time);
        await channel.SendDescriptionAsync(
            new SessionDescription(SdpKind.Offer, Offer),
            TestContext.CancellationToken
        );

        time.Advance(TimeSpan.FromSeconds(waitSeconds));

        _ = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            channel.SendDescriptionAsync(
                new SessionDescription(SdpKind.Answer, Answer),
                TestContext.CancellationToken
            )
        );
        Assert.IsNull(server.AnswerMethod);
        Assert.IsTrue(channel.Answered.IsFaulted);
    }

    // The transport's own format judges the counter-offer: a real offer passes as an offer and its
    // answer as an answer, and neither as the other.
    [TestMethod]
    public async Task OfferCheck_TakesARealOfferAndRefusesItsAnswer()
    {
        using var certificate = Agash.StreamTransport.WebRtc.RtcCertificate.Generate();
        Agash.StreamTransport.WebRtc.Sdp.SdpCodec h264 = new(96, "H264", 90000, null, null, []);
        await using var offerer = new Agash.StreamTransport.WebRtc.PeerConnection(
            new Agash.StreamTransport.WebRtc.PeerConnectionOptions
            {
                Media =
                [
                    new Agash.StreamTransport.WebRtc.MediaLine(
                        "0",
                        Agash.StreamTransport.WebRtc.Sdp.SdpMediaKind.Video,
                        1,
                        [h264]
                    ),
                ],
            },
            certificate
        );
        await using var answerer = new Agash.StreamTransport.WebRtc.PeerConnection(
            new Agash.StreamTransport.WebRtc.PeerConnectionOptions
            {
                Media =
                [
                    new Agash.StreamTransport.WebRtc.MediaLine(
                        "0",
                        Agash.StreamTransport.WebRtc.Sdp.SdpMediaKind.Video,
                        2,
                        [h264]
                    ),
                ],
            },
            certificate
        );
        var offer = offerer.CreateOffer();
        answerer.SetRemoteDescription(offer, Agash.StreamTransport.WebRtc.Sdp.SdpType.Offer);
        string answer = Agash.StreamTransport.WebRtc.Sdp.SdpWriter.Write(answerer.CreateAnswer());

        var format = Agash.StreamTransport.WebRtc.Transport.WebRtcSessionDescriptionFormat.Instance;
        string written = Agash.StreamTransport.WebRtc.Sdp.SdpWriter.Write(offer);

        Assert.IsTrue(format.IsValid(new SessionDescription(SdpKind.Offer, written)));
        Assert.IsTrue(format.IsValid(new SessionDescription(SdpKind.Answer, answer)));
        Assert.IsFalse(format.IsValid(new SessionDescription(SdpKind.Answer, written)));
        Assert.IsFalse(format.IsValid(new SessionDescription(SdpKind.Offer, answer)));
    }

    // RFC 9110 section 8.3.1: media types compare case-insensitively.
    [TestMethod]
    public async Task CounterOffer_MediaTypeInAnyCase_IsRecognised()
    {
        CounterOfferingHandler server = new(type: "Application/SDP");
        await using HttpSdpChannel channel = Channel(server, TimeProvider.System);
        SessionDescription? offered = null;
        channel.DescriptionReceived += description =>
        {
            offered = description;
            return Task.CompletedTask;
        };

        await channel.SendDescriptionAsync(
            new SessionDescription(SdpKind.Offer, Offer),
            TestContext.CancellationToken
        );

        Assert.AreEqual(SdpKind.Offer, offered?.Kind);
    }

    // Section 4.3.2: valid-until, when present, is an HTTP-date; one that is not makes the response
    // invalid rather than starting a fresh 30 seconds.
    [TestMethod]
    public async Task CounterOffer_ValidUntilNotAnHttpDate_IsRefused()
    {
        CounterOfferingHandler server = new(validUntilText: "soon");
        await using HttpSdpChannel channel = Channel(server, TimeProvider.System);

        _ = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            channel.SendDescriptionAsync(
                new SessionDescription(SdpKind.Offer, Offer),
                TestContext.CancellationToken
            )
        );
    }

    // RFC 9110 section 5.6.7: the obsolete RFC 850 and asctime forms are HTTP-dates too.
    [TestMethod]
    [DataRow("Tuesday, 20-Oct-26 12:00:20 GMT")]
    [DataRow("Tue Oct 20 12:00:20 2026")]
    public async Task CounterOffer_ObsoleteHttpDates_AreRead(string date)
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        CounterOfferingHandler server = new(validUntilText: date);
        await using HttpSdpChannel channel = Channel(server, time);
        await channel.SendDescriptionAsync(
            new SessionDescription(SdpKind.Offer, Offer),
            TestContext.CancellationToken
        );

        time.Advance(TimeSpan.FromSeconds(21));

        _ = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            channel.SendDescriptionAsync(
                new SessionDescription(SdpKind.Answer, Answer),
                TestContext.CancellationToken
            )
        );
    }

    // RFC 9725 section 4.3.1: a trickled candidate matches the ICE session's ETag. A counter-offered
    // session given none, in the 406 or the 204, has nothing to match, so a later candidate is not sent.
    [TestMethod]
    [DataRow(null, null, false)]
    [DataRow("\"t406\"", null, true)]
    [DataRow(null, "\"t204\"", true)]
    public async Task LateCandidate_IsTrickledOnlyWithAnIceSessionTag(
        string? offerTag,
        string? answerTag,
        bool trickled
    )
    {
        CounterOfferingHandler server = new(offerTag: offerTag, answerTag: answerTag);
        await using HttpSdpChannel channel = Channel(server, TimeProvider.System);
        await channel.SendDescriptionAsync(
            new SessionDescription(SdpKind.Offer, Offer),
            TestContext.CancellationToken
        );
        await channel.SendDescriptionAsync(
            new SessionDescription(SdpKind.Answer, Answer),
            TestContext.CancellationToken
        );

        await channel.SendCandidateAsync(
            new IceCandidateInit(
                "candidate:2 1 udp 2130706175 198.51.100.7 50001 typ host",
                "v",
                0
            ),
            TestContext.CancellationToken
        );

        Assert.AreEqual(trickled ? 1 : 0, server.Trickles.Count);
        if (trickled)
        {
            Assert.AreEqual(offerTag ?? answerTag, server.Trickles[0]);
        }
    }

    // Section 4.3.2: the server takes the answer with 204; anything else fails the session.
    [TestMethod]
    public async Task RefusedAnswer_FailsTheSession()
    {
        CounterOfferingHandler server = new(patchStatus: HttpStatusCode.BadRequest);
        await using HttpSdpChannel channel = Channel(server, TimeProvider.System);
        await channel.SendDescriptionAsync(
            new SessionDescription(SdpKind.Offer, Offer),
            TestContext.CancellationToken
        );

        _ = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            channel.SendDescriptionAsync(
                new SessionDescription(SdpKind.Answer, Answer),
                TestContext.CancellationToken
            )
        );
        Assert.IsTrue(channel.Answered.IsFaulted);
    }

    private static HttpSdpChannel Channel(HttpMessageHandler server, TimeProvider time) =>
        new(
            new Uri("https://whep.example/whep/live"),
            new HttpSignalingOptions { HttpClient = new HttpClient(server), TimeProvider = time },
            Agash.StreamTransport.WebRtc.Transport.WebRtcSessionDescriptionFormat.Instance,
            NullLogger.Instance
        );

    private sealed class CounterOfferingHandler(
        string body = CounterOffer,
        string type = "application/sdp",
        DateTimeOffset? validUntil = null,
        HttpStatusCode patchStatus = HttpStatusCode.NoContent,
        string? validUntilText = null,
        string? offerTag = null,
        string? answerTag = null
    ) : HttpMessageHandler
    {
        // The If-Match of each trickle PATCH.
        public List<string?> Trickles { get; } = [];

        public HttpMethod? AnswerMethod { get; private set; }

        public Uri? AnswerUri { get; private set; }

        public string? AnswerType { get; private set; }

        public string? AnswerBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.Method == HttpMethod.Post)
            {
                HttpResponseMessage refused = new(HttpStatusCode.NotAcceptable)
                {
                    Content = new StringContent(body, Encoding.UTF8),
                };
                MediaTypeHeaderValue contentType = new(type);
                if (
                    (validUntilText ?? validUntil?.ToString("R", CultureInfo.InvariantCulture)) is
                    { } until
                )
                {
                    contentType.Parameters.Add(
                        new NameValueHeaderValue("valid-until", $"\"{until}\"")
                    );
                }

                if (offerTag is not null)
                {
                    refused.Headers.ETag = EntityTagHeaderValue.Parse(offerTag);
                }

                refused.Content.Headers.ContentType = contentType;
                refused.Headers.Location = new Uri("/whep/live/r1", UriKind.Relative);
                return refused;
            }

            if (
                request.Content?.Headers.ContentType?.MediaType == "application/trickle-ice-sdpfrag"
            )
            {
                Trickles.Add(request.Headers.IfMatch.FirstOrDefault()?.ToString());
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            AnswerMethod = request.Method;
            AnswerUri = request.RequestUri;
            AnswerType = request.Content?.Headers.ContentType?.MediaType;
            AnswerBody = request.Content is { } content
                ? await content.ReadAsStringAsync(cancellationToken)
                : null;
            HttpResponseMessage answered = new(patchStatus);
            if (answerTag is not null)
            {
                answered.Headers.ETag = EntityTagHeaderValue.Parse(answerTag);
            }

            return answered;
        }
    }
}
