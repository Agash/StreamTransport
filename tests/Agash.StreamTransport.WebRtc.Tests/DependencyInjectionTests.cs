using System.Net;
using System.Security.Cryptography;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.Sdp;
using Agash.StreamTransport.WebRtc.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class DependencyInjectionTests
{
    // Negotiation only: ICE starts with the offer and gathers here, never on the machine's interfaces.
    private static readonly InMemoryIceNetwork OfflineNetwork = new();

    [TestMethod]
    public async Task AddStreamTransportWebRtc_ResolvesFactoryAndCreatesPeerConnection()
    {
        var services = new ServiceCollection();
        services.AddStreamTransportWebRtc(scream => scream.MaxBitrateBps = 12_000_000);
        await using var provider = services.BuildServiceProvider();

        var certificate = provider.GetRequiredService<RtcCertificate>();
        Assert.AreEqual(HashAlgorithmName.SHA256, certificate.Fingerprint.Algorithm);
        Assert.AreSame(certificate, provider.GetRequiredService<RtcCertificate>());

        var factory = provider.GetRequiredService<PeerConnectionFactory>();
        var options = new PeerConnectionOptions
        {
            SocketFactory = OfflineNetwork.Factory(IPAddress.Parse("10.0.0.1")),
            Media =
            [
                new MediaLine(
                    "0",
                    SdpMediaKind.Audio,
                    0x1234,
                    [new SdpCodec(111, "opus", 48000, 2, null, [])]
                ),
            ],
        };
        await using var pc = factory.Create(options);

        SdpDescription offer = pc.CreateOffer();
        Assert.AreEqual(1, offer.Media.Length);
        Assert.AreEqual(certificate.Fingerprint, offer.Media[0].Fingerprints[0]);
        Assert.IsTrue(
            pc.CurrentCapacity.TargetBitsPerSecond > 0,
            "the connection runs the registered congestion controller"
        );

        var controller = provider.GetRequiredService<ICongestionController>();
        Assert.IsInstanceOfType<ScreamCongestionController>(controller);
        Assert.IsTrue(controller.Current.TargetBitsPerSecond > 0);
        Assert.IsTrue(
            controller.Current.PacingBitsPerSecond >= controller.Current.TargetBitsPerSecond
        );
    }

    [TestMethod]
    public void AddStreamTransportWebRtc_KeepsAHostsTimeProvider()
    {
        var services = new ServiceCollection();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        services.AddSingleton<TimeProvider>(clock);
        services.AddStreamTransportWebRtc();
        using var provider = services.BuildServiceProvider();

        Assert.AreSame(clock, provider.GetRequiredService<TimeProvider>());
    }
}
