using System.Collections.Concurrent;
using System.Net;
using Agash.StreamTransport.WebRtc.Sdp;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc.Tests;

// An offer that replaces this side's own, as a WHEP counter-offer does (JSEP section 5.7): the side that
// offered first answers, which leaves the other controlling ICE (RFC 8445 section 6.1.1), and the
// session comes up without a role conflict to settle.
[TestClass]
public sealed class CounterOfferTests
{
    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    private static readonly SdpCodec H264 = new(
        96,
        "H264",
        90000,
        null,
        "packetization-mode=1",
        ["nack", "nack pli"]
    );

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(60_000)]
    public async Task RefusedOfferer_AnswersTheCounterOffer_AsTheControlledAgent()
    {
        InMemoryIceNetwork network = new();
        using RecordingLogs playerLogs = new();
        await using PeerConnection player = new(
            Options(network, "10.0.0.1", 0xAAAA_0001),
            Certificate,
            playerLogs
        );
        await using PeerConnection server = new(
            Options(network, "10.0.0.2", 0xBBBB_0001),
            Certificate
        );
        player.LocalIceCandidate += server.AddRemoteIceCandidate;
        server.LocalIceCandidate += player.AddRemoteIceCandidate;
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int up = 0;
        void OnState(PeerConnectionState state)
        {
            if (state == PeerConnectionState.Connected && Interlocked.Increment(ref up) == 2)
            {
                connected.TrySetResult();
            }
        }

        player.StateChanged += OnState;
        server.StateChanged += OnState;

        // The player offers; the server refuses it and offers instead.
        _ = player.CreateOffer();
        player.SetRemoteDescription(server.CreateOffer(), SdpType.Offer);
        server.SetRemoteDescription(player.CreateAnswer(), SdpType.Answer);

        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        Assert.Contains("ICE role is Controlled", playerLogs.Messages);
        Assert.DoesNotContain(
            static (string m) => m.Contains("conflict", StringComparison.OrdinalIgnoreCase),
            playerLogs.Messages
        );
    }

    private static PeerConnectionOptions Options(
        InMemoryIceNetwork network,
        string address,
        uint ssrc
    ) =>
        new()
        {
            Media = [new MediaLine("0", SdpMediaKind.Video, ssrc, [H264])],
            SocketFactory = network.Factory(IPAddress.Parse(address)),
        };

    private sealed class RecordingLogs : ILoggerFactory, ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public List<string> Messages => [.. _messages];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => _messages.Enqueue(formatter(state, exception));

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }
    }
}
