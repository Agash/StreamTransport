using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.CongestionControl;
using Agash.StreamTransport.WebRtc.DependencyInjection;
using Agash.StreamTransport.WebRtc.Sdp;
using Microsoft.Extensions.DependencyInjection;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class DependencyInjectionTests
{
    [TestMethod]
    public async Task AddStreamTransportWebRtc_ResolvesFactoryAndCreatesPeerConnection()
    {
        var services = new ServiceCollection();
        services.AddStreamTransportWebRtc(scream => scream.MaxBitrateBps = 12_000_000);
        await using var provider = services.BuildServiceProvider();

        var dtls = provider.GetRequiredService<IDtlsTransportFactory>();
        Assert.AreEqual("sha-256", dtls.LocalFingerprint.Algorithm);

        var factory = provider.GetRequiredService<PeerConnectionFactory>();
        var options = new PeerConnectionOptions
        {
            Media = [new MediaLine("0", SdpMediaKind.Audio, 0x1234, [new SdpCodec(111, "opus", 48000, 2, null, [])])],
        };
        await using var pc = factory.Create(options);

        SdpDescription offer = pc.CreateOffer();
        Assert.AreEqual(1, offer.Media.Count);
        Assert.AreEqual(dtls.LocalFingerprint.ToSdpValue(), offer.Media[0].Fingerprint.ToSdpValue());

        var controller = provider.GetRequiredService<INetworkController>();
        Assert.IsInstanceOfType<ScreamCongestionController>(controller);
        Assert.IsTrue(controller.CurrentEstimate.TargetBitrateBps > 0);
        Assert.IsTrue(controller.CurrentEstimate.PacingRateBps >= controller.CurrentEstimate.TargetBitrateBps);
    }
}
