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
        + "m=video 9 UDP/TLS/RTP/SAVPF 102\r\na=mid:v\r\na=setup:actpass\r\n"
        + "a=ice-ufrag:Srv1\r\na=ice-pwd:ServerPasswordServerPassw\r\n";

    private const string Answer =
        "v=0\r\no=- 1 3 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"
        + "m=video 9 UDP/TLS/RTP/SAVPF 102\r\na=mid:v\r\na=setup:active\r\n"
        + "a=ice-ufrag:Cli1\r\na=ice-pwd:ClientPasswordClientPassw\r\n";

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

    // Section 4.3.3: without an application/sdp offer in it, a 406 is not a counter-offer.
    [TestMethod]
    public async Task NotAcceptableWithoutAnOffer_IsARefusal()
    {
        CounterOfferingHandler server = new(body: "no", type: "text/plain");
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
            NullLogger.Instance
        );

    private sealed class CounterOfferingHandler(
        string body = CounterOffer,
        string type = "application/sdp",
        DateTimeOffset? validUntil = null,
        HttpStatusCode patchStatus = HttpStatusCode.NoContent
    ) : HttpMessageHandler
    {
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
                if (validUntil is { } until)
                {
                    contentType.Parameters.Add(
                        new NameValueHeaderValue(
                            "valid-until",
                            $"\"{until.ToString("R", CultureInfo.InvariantCulture)}\""
                        )
                    );
                }

                refused.Content.Headers.ContentType = contentType;
                refused.Headers.Location = new Uri("/whep/live/r1", UriKind.Relative);
                return refused;
            }

            AnswerMethod = request.Method;
            AnswerUri = request.RequestUri;
            AnswerType = request.Content?.Headers.ContentType?.MediaType;
            AnswerBody = request.Content is { } content
                ? await content.ReadAsStringAsync(cancellationToken)
                : null;
            return new HttpResponseMessage(patchStatus);
        }
    }
}
