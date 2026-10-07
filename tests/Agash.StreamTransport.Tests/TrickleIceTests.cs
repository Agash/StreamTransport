using System.Net;
using System.Text;
using Agash.StreamTransport.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.Tests;

// Trickled candidates over WHIP and WHEP (RFC 9725 section 4.3, RFC 8840): the fragment format, and the
// client's PATCH requests against a recording HTTP handler.
[TestClass]
public sealed class TrickleIceTests
{
    private const string Offer =
        "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0 1\r\n"
        + "m=video 9 UDP/TLS/RTP/SAVPF 96\r\nc=IN IP4 0.0.0.0\r\na=mid:0\r\n"
        + "a=ice-ufrag:EsAw\r\na=ice-pwd:P2uYro0UCOQ4zxjKXaWCBui1\r\n"
        + "a=candidate:1 1 udp 2130706431 192.0.2.1 50000 typ host\r\n"
        + "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=mid:1\r\n";

    private const string Cellular = "candidate:2 1 udp 2130706175 198.51.100.7 50001 typ host";
    private const string Reflexive =
        "candidate:3 1 udp 1694498815 203.0.113.9 61000 typ srflx raddr 198.51.100.7 rport 50001";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ForFirstSection_TakesTheOffererTaggedSection()
    {
        var fragment = TrickleIceFragment.ForFirstSection(Offer, [Cellular]);

        Assert.AreEqual("EsAw", fragment.UsernameFragment);
        Assert.AreEqual("P2uYro0UCOQ4zxjKXaWCBui1", fragment.Password);
        Assert.AreEqual("video 9 UDP/TLS/RTP/SAVPF 96", fragment.MediaLine);
        Assert.AreEqual("0", fragment.Mid);
        Assert.AreEqual(
            "a=ice-ufrag:EsAw\r\na=ice-pwd:P2uYro0UCOQ4zxjKXaWCBui1\r\n"
                + "m=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\na="
                + Cellular
                + "\r\n",
            fragment.Format()
        );
    }

    [TestMethod]
    public void TryParse_ReadsTheRfc9725Example()
    {
        // RFC 9725 figure 3, its candidate lines unfolded.
        const string body =
            "a=group:BUNDLE 0 1\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=mid:0\r\na=ice-ufrag:EsAw\r\n"
            + "a=ice-pwd:P2uYro0UCOQ4zxjKXaWCBui1\r\n"
            + "a=candidate:1387637174 1 udp 2122260223 192.0.2.1 61764 typ host generation 0 ufrag EsAw network-id 1\r\n"
            + "a=candidate:3471623853 1 udp 2122194687 198.51.100.2 61765 typ host generation 0 ufrag EsAw network-id 2\r\n"
            + "a=end-of-candidates\r\n";

        Assert.IsTrue(TrickleIceFragment.TryParse(body, out TrickleIceFragment fragment));
        Assert.AreEqual("EsAw", fragment.UsernameFragment);
        Assert.AreEqual("0", fragment.Mid);
        Assert.HasCount(2, fragment.Candidates);
        StringAssert.StartsWith(fragment.Candidates[1], "candidate:3471623853 1 udp");
        Assert.IsTrue(fragment.EndOfCandidates);
    }

    [TestMethod]
    [DataRow("hello")]
    [DataRow("a=candidate:1 1 udp 1 192.0.2.1 1 typ host")]
    public void TryParse_RefusesWhatIsNoFragment(string body) =>
        Assert.IsFalse(TrickleIceFragment.TryParse(body, out _));

    [TestMethod]
    public void ApplyTo_PutsTheFragmentsIceInPlaceOfTheDescriptions()
    {
        TrickleIceFragment restart = new(
            "NewU",
            "NewPasswordNewPassword",
            TrickleIceFragment.DefaultMediaLine,
            "0",
            [Cellular]
        );
        const string answer =
            "v=0\r\ns=-\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\na=ice-ufrag:OldU\r\n"
            + "a=ice-pwd:OldPasswordOldPassword\r\na=candidate:1 1 udp 1 192.0.2.1 1 typ host\r\n"
            + "a=end-of-candidates\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=mid:1\r\n"
            + "a=ice-ufrag:OldU\r\na=ice-pwd:OldPasswordOldPassword\r\n";

        string applied = restart.ApplyTo(answer);

        Assert.AreEqual(
            "v=0\r\ns=-\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\na=ice-ufrag:NewU\r\n"
                + "a=ice-pwd:NewPasswordNewPassword\r\na="
                + Cellular
                + "\r\n"
                + "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=mid:1\r\n"
                + "a=ice-ufrag:NewU\r\na=ice-pwd:NewPasswordNewPassword\r\n",
            applied
        );
        CollectionAssert.AreEqual(
            new[] { "candidate:1 1 udp 2130706431 192.0.2.1 50000 typ host" },
            TrickleIceFragment.FromFirstSection(Offer).Candidates.ToArray()
        );
    }

    [TestMethod]
    public async Task Client_RestartsIceInAPatch_AndTricklesUnderTheNewSession()
    {
        RecordingHandler server = new(HttpStatusCode.NoContent)
        {
            Restart =
                "a=ice-ufrag:SrvU\r\na=ice-pwd:ServerPasswordServerPassw\r\n"
                + "m=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\n"
                + "a=candidate:7 1 udp 2130706431 203.0.113.5 40000 typ host\r\n",
        };
        await using HttpSdpChannel channel = Channel(server);
        List<SessionDescription> answers = [];
        channel.DescriptionReceived += d =>
        {
            answers.Add(d);
            return Task.CompletedTask;
        };
        await channel.SendAsync(
            new SessionDescription(SdpKind.Offer, Offer),
            TestContext.CancellationToken
        );

        string restartOffer = Offer
            .Replace("EsAw", "Rst1", StringComparison.Ordinal)
            .Replace(
                "P2uYro0UCOQ4zxjKXaWCBui1",
                "RestartPasswordRestartPa",
                StringComparison.Ordinal
            );
        await channel.SendAsync(
            new SessionDescription(SdpKind.Offer, restartOffer),
            TestContext.CancellationToken
        );
        await channel.SendAsync(
            new IceCandidateInit(Cellular, "0", 0),
            TestContext.CancellationToken
        );

        Assert.HasCount(2, server.Patches);
        Assert.AreEqual("*", server.Patches[0].IfMatch);
        Assert.IsTrue(
            TrickleIceFragment.TryParse(server.Patches[0].Body, out TrickleIceFragment sent)
        );
        Assert.AreEqual("Rst1", sent.UsernameFragment);
        Assert.HasCount(1, sent.Candidates, "the restart carries the new session's candidates.");
        Assert.HasCount(2, answers);
        StringAssert.Contains(answers[1].Sdp, "a=ice-ufrag:SrvU");
        StringAssert.Contains(answers[1].Sdp, "203.0.113.5 40000");
        Assert.AreEqual(
            "\"r2\"",
            server.Patches[1].IfMatch,
            "a trickle carries the new session's tag."
        );
        Assert.IsTrue(
            TrickleIceFragment.TryParse(server.Patches[1].Body, out TrickleIceFragment next)
        );
        Assert.AreEqual("Rst1", next.UsernameFragment);
    }

    [TestMethod]
    public async Task Client_BuffersUntilAnswered_ThenPatchesWithTheIceSessionTag()
    {
        RecordingHandler server = new(HttpStatusCode.NoContent);
        await using HttpSdpChannel channel = Channel(server);

        // Gathered before the server answered: held until the resource exists.
        await channel.SendAsync(
            new IceCandidateInit(Cellular, "0", 0),
            TestContext.CancellationToken
        );
        Assert.IsEmpty(server.Patches);

        await channel.SendAsync(
            new SessionDescription(SdpKind.Offer, Offer),
            TestContext.CancellationToken
        );
        await channel.SendAsync(
            new IceCandidateInit(Reflexive, "0", 0),
            TestContext.CancellationToken
        );

        Assert.HasCount(2, server.Patches);
        (string body, string? ifMatch, string? type, Uri uri) = server.Patches[0];
        Assert.AreEqual(new Uri("http://whip.example/whip/live/r1"), uri);
        Assert.AreEqual("\"r1\"", ifMatch);
        Assert.AreEqual(TrickleIceFragment.MediaType, type);
        Assert.IsTrue(TrickleIceFragment.TryParse(body, out TrickleIceFragment first));
        Assert.AreEqual("EsAw", first.UsernameFragment);
        CollectionAssert.AreEqual(new[] { Cellular }, first.Candidates.ToArray());
        Assert.IsTrue(
            TrickleIceFragment.TryParse(server.Patches[1].Body, out TrickleIceFragment next)
        );
        CollectionAssert.AreEqual(new[] { Reflexive }, next.Candidates.ToArray());
    }

    [TestMethod]
    public async Task Client_ServerThatTakesNoTrickle_IsNotAskedAgain()
    {
        RecordingHandler server = new(HttpStatusCode.MethodNotAllowed);
        await using HttpSdpChannel channel = Channel(server);
        await channel.SendAsync(
            new SessionDescription(SdpKind.Offer, Offer),
            TestContext.CancellationToken
        );

        await channel.SendAsync(
            new IceCandidateInit(Cellular, "0", 0),
            TestContext.CancellationToken
        );
        await channel.SendAsync(
            new IceCandidateInit(Reflexive, "0", 0),
            TestContext.CancellationToken
        );

        Assert.HasCount(1, server.Patches);
    }

    private static HttpSdpChannel Channel(RecordingHandler server) =>
        new(
            new Uri("http://whip.example/whip/live"),
            new HttpSignalingOptions { HttpClient = new HttpClient(server) },
            NullLogger.Instance
        );

    // A WHIP server that answers the offer with a resource and an ETag, and records each PATCH.
    private sealed class RecordingHandler(HttpStatusCode patchStatus) : HttpMessageHandler
    {
        public List<(string Body, string? IfMatch, string? Type, Uri Uri)> Patches { get; } = [];

        // The server's fragment for an ICE restart (a PATCH with If-Match "*"), answered 200 with tag r2.
        public string? Restart { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.Method == HttpMethod.Post)
            {
                HttpResponseMessage created = new(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        "v=0\r\ns=-\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\n"
                            + "a=ice-ufrag:Ans1\r\na=ice-pwd:AnswerPasswordAnswerPassw\r\n"
                            + "a=candidate:5 1 udp 2130706431 192.0.2.50 30000 typ host\r\n",
                        Encoding.UTF8,
                        "application/sdp"
                    ),
                };
                created.Headers.Location = new Uri("/whip/live/r1", UriKind.Relative);
                created.Headers.ETag = new("\"r1\"");
                return created;
            }

            if (request.Method == HttpMethod.Patch)
            {
                Patches.Add(
                    (
                        await request.Content!.ReadAsStringAsync(cancellationToken),
                        request.Headers.IfMatch.FirstOrDefault()?.ToString(),
                        request.Content.Headers.ContentType?.MediaType,
                        request.RequestUri!
                    )
                );
                if (Restart is { } fragment && request.Headers.IfMatch.Any(t => t.Tag == "*"))
                {
                    HttpResponseMessage restarted = new(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            fragment,
                            Encoding.UTF8,
                            TrickleIceFragment.MediaType
                        ),
                    };
                    restarted.Headers.ETag = new("\"r2\"");
                    return restarted;
                }

                return new HttpResponseMessage(patchStatus);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
