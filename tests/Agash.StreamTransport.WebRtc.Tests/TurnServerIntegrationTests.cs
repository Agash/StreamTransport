using System.Net;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Turn;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// Allocates on a real TURN server (coturn), named by <c>STREAMTRANSPORT_TURN</c> as
/// <c>host,port,tlsPort,username,password</c>. Skipped when the variable is unset.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class TurnServerIntegrationTests
{
    [TestMethod]
    [DataRow(TurnTransport.Udp)]
    [DataRow(TurnTransport.Tcp)]
    [DataRow(TurnTransport.Tls)]
    [Timeout(30_000)]
    public async Task Allocate_RealServer_EveryFamilyAndTransport(TurnTransport transport)
    {
        TurnServer server = Server(transport);
        IPAddress[] addresses = IPAddress.TryParse(server.Host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(server.Host);
        foreach (IPAddress address in addresses)
        {
            using TurnAllocation allocation = await TurnAllocation.AllocateAsync(
                server,
                new IPEndPoint(address, server.Port),
                TimeProvider.System,
                NullLogger.Instance,
                CancellationToken.None
            );
            Assert.AreEqual(address.AddressFamily, allocation.LocalEndPoint.AddressFamily);
            Assert.AreNotEqual(0, allocation.LocalEndPoint.Port);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task RelayPolicy_RealServer_TwoAgentsConnect()
    {
        TurnServer server = Server(TurnTransport.Udp);
        var offererCredentials = IceCredentials.Generate();
        var answererCredentials = IceCredentials.Generate();
        await using var offerer = new IceAgent(
            offererCredentials,
            IceRole.Controlling,
            transportPolicy: IceTransportPolicy.Relay
        );
        await using var answerer = new IceAgent(
            answererCredentials,
            IceRole.Controlled,
            transportPolicy: IceTransportPolicy.Relay
        );
        offerer.AddTurnServer(server);
        answerer.AddTurnServer(server with { Transport = TurnTransport.Tcp });
        offerer.SetRemoteCredentials(answererCredentials);
        answerer.SetRemoteCredentials(offererCredentials);
        offerer.LocalCandidateGathered += answerer.AddRemoteCandidate;
        answerer.LocalCandidateGathered += offerer.AddRemoteCandidate;
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        offerer.StateChanged += s =>
        {
            if (s == IceConnectionState.Connected)
            {
                connected.TrySetResult();
            }
        };

        offerer.Start();
        answerer.Start();
        await connected.Task;

        Assert.IsNotNull(offerer.SelectedPath);
    }

    private static TurnServer Server(TurnTransport transport)
    {
        string? setting = Environment.GetEnvironmentVariable("STREAMTRANSPORT_TURN");
        if (string.IsNullOrEmpty(setting))
        {
            Assert.Inconclusive("STREAMTRANSPORT_TURN is not set.");
        }

        string[] parts = setting.Split(',');
        int port = int.Parse(
            parts[transport == TurnTransport.Tls ? 2 : 1],
            System.Globalization.CultureInfo.InvariantCulture
        );
        return new TurnServer(parts[0], port, transport, parts[3], parts[4])
        {
            // The lab server's certificate is self-signed.
            CertificateValidation = static (_, _, _, _) => true,
        };
    }
}
