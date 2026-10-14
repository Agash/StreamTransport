using System.Buffers.Binary;
using System.Net;
using System.Text;
using Agash.StreamTransport.Stun;
using Agash.StreamTransport.WebRtc.Ice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.WebRtc.Tests;

// Nomination and consent as the specifications put them: RFC 8445 section 8.1.1 (one nomination without
// renomination), draft-thatcher-ice-renomination (numbered renominations, the highest wins), RFC 7675
// section 5.1 (consent checks 4 to 6 s apart), RFC 8445 section 7.3 (a request for another username is not
// answered) and the W3C disconnected state.
[TestClass]
public sealed class IceNominationTests
{
    private static readonly IPAddress[] TwoA =
    [
        IPAddress.Parse("10.0.0.1"),
        IPAddress.Parse("10.1.0.1"),
    ];
    private static readonly IPAddress[] TwoB =
    [
        IPAddress.Parse("10.0.0.2"),
        IPAddress.Parse("10.1.0.2"),
    ];

    [TestMethod]
    public void WithoutRenomination_OnePairIsNominated_AndAQuietPathTakesARestart()
    {
        IceSwitchboard board = new();
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = board.Pair(
            TwoA,
            TwoB,
            options: IceOptions.Trickle | IceOptions.Ice2
        );
        Assert.IsTrue(board.RunUntil(() => a.Connected && b.Connected, TimeSpan.FromSeconds(10)));
        board.Run(TimeSpan.FromSeconds(3));
        IPEndPoint selected = a.Endpoints[a.Machine.Selected!.Value.Local];
        HashSet<IPEndPoint> nominatingSources = [];
        board.Hold(
            (source, data) =>
            {
                if (IsRequestWith(data, StunAttributeType.UseCandidate))
                {
                    _ = nominatingSources.Add(source);
                }

                return false;
            }
        );

        board.Cut(selected.Address);
        _ = board.Stream(TimeSpan.FromSeconds(10));

        Assert.IsFalse(a.Machine.CanMovePath);
        Assert.AreEqual(IceConnectionState.Disconnected, a.Machine.State);
        Assert.AreEqual(
            selected,
            a.Endpoints[a.Machine.Selected!.Value.Local],
            "no second nomination"
        );
        Assert.IsTrue(nominatingSources.All(source => source.Equals(selected)));
    }

    [TestMethod]
    public void WithRenomination_AFailoverNominatesWithAHigherNumber()
    {
        IceSwitchboard board = new();
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = board.Pair(TwoA, TwoB);
        Assert.IsTrue(board.RunUntil(() => a.Connected && b.Connected, TimeSpan.FromSeconds(10)));
        _ = board.Stream(TimeSpan.FromSeconds(3));
        IPEndPoint first = a.Endpoints[a.Machine.Selected!.Value.Local];
        Dictionary<IPEndPoint, uint> highest = [];
        board.Hold(
            (source, data) =>
            {
                if (TryNomination(data, out uint number))
                {
                    highest[source] = Math.Max(highest.GetValueOrDefault(source), number);
                }

                return false;
            }
        );
        _ = board.Stream(TimeSpan.FromSeconds(6));

        board.Cut(first.Address);
        _ = board.Stream(TimeSpan.FromSeconds(8));

        IPEndPoint second = a.Endpoints[a.Machine.Selected!.Value.Local];
        Assert.AreNotEqual(first, second);
        Assert.IsTrue(a.Machine.CanMovePath);
        Assert.IsGreaterThan(highest[first], highest[second]);
        Assert.AreEqual(IceConnectionState.Connected, a.Machine.State);
        Assert.AreEqual(
            second.Address,
            b.Machine.Selected!.Value.Remote.Address,
            "the peer follows"
        );
    }

    // The renomination with the highest number wins, whatever order the checks arrive in.
    [TestMethod]
    public void Controlled_TakesTheHighestNomination_InWhateverOrderItArrives()
    {
        ScriptedControlling peer = new();
        IPEndPoint x = new(IPAddress.Parse("10.0.0.2"), 6000);
        IPEndPoint y = new(IPAddress.Parse("10.1.0.2"), 6000);
        peer.ValidatePairs(x, y);

        peer.Nominate(y, 3);
        peer.Nominate(x, 2);

        Assert.AreEqual(y, peer.Machine.Selected!.Value.Remote);
    }

    [TestMethod]
    public void RequestForAnotherUsername_IsNotAnswered()
    {
        ScriptedControlling peer = new();
        IPEndPoint x = new(IPAddress.Parse("10.0.0.2"), 6000);

        peer.Send(x, peer.Request(username: "nope:" + peer.Credentials.UsernameFragment));

        Assert.IsFalse(peer.Answers().Any());
    }

    // RFC 8489 sections 9.1.3 and 6.3.1: no MESSAGE-INTEGRITY is a 400, a bad USERNAME a 401, an unknown
    // comprehension-required attribute a 420.
    [TestMethod]
    public void BadRequests_AreAnsweredWithTheirErrors()
    {
        ScriptedControlling peer = new();
        IPEndPoint x = new(IPAddress.Parse("10.0.0.2"), 6000);

        peer.Send(x, peer.Request(integrity: false));
        peer.Send(x, peer.Request(username: "nope:" + peer.Credentials.UsernameFragment));
        peer.Send(x, peer.Request(unknownRequired: 0x7F01));

        CollectionAssert.AreEqual(new[] { 400, 401, 420 }, peer.Errors().ToArray());
    }

    [TestMethod]
    public void ConsentChecks_OnTheSelectedPair_AreFourToSixSecondsApart()
    {
        IceSwitchboard board = new();
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = board.Pair(
            [IPAddress.Parse("10.0.0.1")],
            [IPAddress.Parse("10.0.0.2")]
        );
        Assert.IsTrue(board.RunUntil(() => a.Connected && b.Connected, TimeSpan.FromSeconds(10)));
        board.Run(TimeSpan.FromSeconds(10));
        IPEndPoint source = a.Endpoints[a.Machine.Selected!.Value.Local];
        List<TimeSpan> sent = [];
        board.Hold(
            (from, data) =>
            {
                if (from.Equals(source) && IsRequestWith(data, StunAttributeType.Username))
                {
                    sent.Add(board.Now);
                }

                return false;
            }
        );

        _ = board.Stream(TimeSpan.FromSeconds(120));

        TimeSpan[] gaps = [.. sent.Zip(sent.Skip(1), static (p, n) => n - p)];
        Assert.IsGreaterThan(15, gaps.Length);
        Assert.IsTrue(
            gaps.All(static g => g >= TimeSpan.FromSeconds(4) && g <= TimeSpan.FromSeconds(6.05)),
            string.Join(", ", gaps)
        );
        Assert.IsGreaterThan(0.5, gaps.Max().TotalSeconds - gaps.Min().TotalSeconds, "randomised");
    }

    [TestMethod]
    public void SilentSelectedPath_IsDisconnected_ThenConnectedWhenItReceivesAgain()
    {
        IceSwitchboard board = new();
        var address = IPAddress.Parse("10.0.0.1");
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = board.Pair(
            [address],
            [IPAddress.Parse("10.0.0.2")]
        );
        Assert.IsTrue(board.RunUntil(() => a.Connected && b.Connected, TimeSpan.FromSeconds(10)));
        _ = board.Stream(TimeSpan.FromSeconds(2));

        board.Cut(address);
        _ = board.Stream(TimeSpan.FromSeconds(4));
        Assert.AreEqual(IceConnectionState.Disconnected, a.Machine.State);

        board.Uncut(address);
        _ = board.Stream(TimeSpan.FromSeconds(1));
        Assert.AreEqual(IceConnectionState.Connected, a.Machine.State);
    }

    private static bool IsRequestWith(byte[] data, StunAttributeType attribute) =>
        StunMessageReader.TryParse(data, out StunMessageReader stun)
        && stun.Class == StunMessageClass.Request
        && stun.TryFindAttribute(attribute, out _);

    private static bool TryNomination(byte[] data, out uint number)
    {
        number = 0;
        return StunMessageReader.TryParse(data, out StunMessageReader stun)
            && stun.Class == StunMessageClass.Request
            && stun.TryGetUInt32(StunAttributeType.Nomination, out number);
    }

    // A controlling peer played by hand against a controlled machine: it answers the machine's checks and
    // sends the requests a test asks for.
    private sealed class ScriptedControlling
    {
        private readonly IPEndPoint _local = new(IPAddress.Parse("10.0.0.1"), 5000);
        private readonly List<IceTransmit> _sent = [];
        private TimeSpan _now = TimeSpan.Zero;

        public ScriptedControlling()
        {
            Machine = new IceStateMachine(
                IceCredentials.Generate(),
                IceRole.Controlled,
                IceTimings.Default,
                NullLogger.Instance,
                random: new Random(1)
            );
            Machine.SetRemoteCredentials(Credentials);
            Machine.SetRemoteOptions(IceOptions.Supported);
            Machine.AddLocalEndpoint(0, _local);
            Machine.Start(_now);
        }

        public IceStateMachine Machine { get; }

        // The scripted peer's own credentials.
        public IceCredentials Credentials { get; } = IceCredentials.Generate();

        // Makes the machine's pairs to each remote valid: it checks them and the peer answers.
        public void ValidatePairs(params IPEndPoint[] remotes)
        {
            foreach (IPEndPoint remote in remotes)
            {
                Assert.IsTrue(
                    IceCandidate.TryParse(
                        $"candidate:{remote.GetHashCode() & 0xFFFF} 1 udp 2130706431 {remote.Address} {remote.Port} typ host",
                        out IceCandidate candidate
                    )
                );
                Machine.AddRemoteCandidate(candidate);
            }

            for (int i = 0; i < 200; i++)
            {
                _now += TimeSpan.FromMilliseconds(50);
                Machine.HandleTimeout(_now);
                AnswerChecks();
            }
        }

        public void Nominate(IPEndPoint from, uint number) =>
            Send(from, Request(useCandidate: true, nomination: number));

        public byte[] Request(
            string? username = null,
            bool useCandidate = false,
            uint? nomination = null,
            bool integrity = true,
            ushort? unknownRequired = null
        )
        {
            byte[] buffer = new byte[256];
            StunMessageWriter writer = new(
                buffer,
                StunMessageClass.Request,
                StunMethod.Binding,
                Guid.NewGuid().ToByteArray()[..StunHeader.TransactionIdLength]
            );
            writer.AddAttribute(
                StunAttributeType.Username,
                Encoding.UTF8.GetBytes(
                    username
                        ?? IceCredentials.CheckUsername(
                            Machine.LocalCredentials.UsernameFragment,
                            Credentials.UsernameFragment
                        )
                )
            );
            Span<byte> value = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(value, 0x6E7F1EFF);
            writer.AddAttribute(StunAttributeType.Priority, value[..4]);
            BinaryPrimitives.WriteUInt64BigEndian(value, ulong.MaxValue);
            writer.AddAttribute(StunAttributeType.IceControlling, value);
            if (useCandidate)
            {
                writer.AddAttribute(StunAttributeType.UseCandidate, default);
            }

            if (nomination is { } number)
            {
                BinaryPrimitives.WriteUInt32BigEndian(value, number);
                writer.AddAttribute(StunAttributeType.Nomination, value[..4]);
            }

            if (unknownRequired is { } type)
            {
                writer.AddAttribute((StunAttributeType)type, [0, 0, 0, 0]);
            }

            if (integrity)
            {
                writer.AddMessageIntegrity(
                    Encoding.UTF8.GetBytes(Machine.LocalCredentials.Password)
                );
            }

            writer.AddFingerprint();
            return buffer[..writer.Length];
        }

        public void Send(IPEndPoint from, byte[] datagram)
        {
            Assert.IsTrue(StunMessageReader.TryParse(datagram, out StunMessageReader stun));
            Machine.HandleStun(0, from, stun, _now);
            _now += TimeSpan.FromMilliseconds(50);
            Machine.HandleTimeout(_now);
            AnswerChecks();
        }

        // The success responses the machine sent since the last look.
        public IEnumerable<IceTransmit> Answers()
        {
            AnswerChecks();
            return _sent.Where(static t =>
                StunMessageReader.TryParse(t.Data, out StunMessageReader stun)
                && stun.Class == StunMessageClass.SuccessResponse
            );
        }

        // The error codes of the error responses the machine sent.
        public IEnumerable<int> Errors()
        {
            AnswerChecks();
            foreach (IceTransmit t in _sent)
            {
                if (
                    StunMessageReader.TryParse(t.Data, out StunMessageReader stun)
                    && stun.Class == StunMessageClass.ErrorResponse
                    && stun.TryGetErrorCode(out int code)
                )
                {
                    yield return code;
                }
            }
        }

        private void AnswerChecks()
        {
            while (Machine.TryPollTransmit(out IceTransmit transmit))
            {
                _sent.Add(transmit);
                if (
                    !StunMessageReader.TryParse(transmit.Data, out StunMessageReader request)
                    || request.Class != StunMessageClass.Request
                )
                {
                    continue;
                }

                byte[] buffer = new byte[128];
                StunMessageWriter writer = new(
                    buffer,
                    StunMessageClass.SuccessResponse,
                    StunMethod.Binding,
                    request.TransactionId
                );
                writer.AddXorMappedAddress(_local);
                writer.AddMessageIntegrity(Encoding.UTF8.GetBytes(Credentials.Password));
                writer.AddFingerprint();
                Assert.IsTrue(
                    StunMessageReader.TryParse(
                        buffer.AsSpan(0, writer.Length),
                        out StunMessageReader response
                    )
                );
                Machine.HandleStun(transmit.Local, transmit.Destination, response, _now);
            }
        }
    }
}
