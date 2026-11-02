using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Agash.StreamTransport.Adaptation;

namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>
/// DSCP marking on Windows through qWAVE (qos2.h). Winsock takes no per-datagram DSCP, so a socket's traffic
/// to one destination is a flow with a traffic type, which the system marks with its DSCP: video makes the
/// flow audio-video, audio alone voice, lower effort background. Setting an exact DSCP needs the
/// Administrators or Network Configuration Operators group, so the traffic type is what this uses. A flow
/// the system refuses (no qWAVE service, a policy) stays unmarked: marking is advice to the network.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsQosFlows : IDisposable
{
    // qos2.h.
    private const int TrafficTypeBestEffort = 0;
    private const int TrafficTypeBackground = 1;
    private const int TrafficTypeAudioVideo = 3;
    private const int TrafficTypeVoice = 4;
    private const uint NonAdaptiveFlow = 0x00000002;
    private const int SetTrafficType = 0;

    private readonly Lock _gate = new();
    private readonly Dictionary<IPEndPoint, Flow> _flows = [];
    private nint _handle;
    private bool _unavailable;

    /// <summary>Marks the socket's flow to a destination for a packet's DSCP, creating or raising it.</summary>
    /// <param name="socket">The socket.</param>
    /// <param name="destination">Where the packet goes.</param>
    /// <param name="dscp">Its DSCP.</param>
    public void Mark(Socket socket, IPEndPoint destination, DiffServCodepoint dscp)
    {
        int type = TypeFor(dscp);
        lock (_gate)
        {
            if (_unavailable)
            {
                return;
            }

            if (_flows.TryGetValue(destination, out Flow? flow))
            {
                // Video outranks audio in one flow: EF for video would be dropped as excess (RFC 8837).
                if (
                    flow.Failed
                    || flow.Type == type
                    || flow.Type == TrafficTypeAudioVideo
                    || type == TrafficTypeBestEffort
                )
                {
                    return;
                }

                int raised = type;
                flow.Type =
                    QOSSetFlow(_handle, flow.Id, SetTrafficType, sizeof(int), &raised, 0, null) != 0
                        ? type
                        : flow.Type;
                return;
            }

            if (type == TrafficTypeBestEffort || !Open())
            {
                return;
            }

            SocketAddress address = destination.Serialize();
            Span<byte> name = stackalloc byte[address.Size];
            address.Buffer.Span[..address.Size].CopyTo(name);
            uint id = 0;
            fixed (byte* pName = name)
            {
                bool added =
                    QOSAddSocketToFlow(_handle, socket.Handle, pName, type, NonAdaptiveFlow, &id)
                    != 0;

                // Deliberately not logged per packet: an unmarked flow still carries the media, and the
                // refusal is the system's policy, the same for every flow.
                _flows[destination] = new Flow(id, added ? type : -1, !added);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle != 0)
            {
                _ = QOSCloseHandle(_handle);
                _handle = 0;
            }

            _flows.Clear();
        }
    }

    private static int TypeFor(DiffServCodepoint dscp) =>
        dscp switch
        {
            DiffServCodepoint.Expedited => TrafficTypeVoice,
            DiffServCodepoint.Af41 or DiffServCodepoint.Af42 or DiffServCodepoint.Af43 =>
                TrafficTypeAudioVideo,
            DiffServCodepoint.LowerEffort => TrafficTypeBackground,
            _ => TrafficTypeBestEffort,
        };

    private bool Open()
    {
        if (_handle != 0)
        {
            return true;
        }

        try
        {
            // QOS_VERSION { USHORT Major = 1; USHORT Minor = 0; }
            uint version = 1;
            nint handle;
            if (QOSCreateHandle(&version, &handle) != 0)
            {
                _handle = handle;
                return true;
            }
        }
        catch (DllNotFoundException)
        {
            // Deliberately not logged: Windows Server without the qWAVE feature has no qwave.dll; media is
            // sent unmarked, as on a network that bleaches the field.
        }

        _unavailable = true;
        return false;
    }

    private sealed class Flow(uint id, int type, bool failed)
    {
        public uint Id { get; } = id;

        public int Type { get; set; } = type;

        public bool Failed { get; } = failed;
    }

    [LibraryImport("qwave.dll", SetLastError = true)]
    private static partial int QOSCreateHandle(uint* version, nint* handle);

    [LibraryImport("qwave.dll", SetLastError = true)]
    private static partial int QOSCloseHandle(nint handle);

    [LibraryImport("qwave.dll", SetLastError = true)]
    private static partial int QOSAddSocketToFlow(
        nint handle,
        nint socket,
        byte* destination,
        int trafficType,
        uint flags,
        uint* flowId
    );

    [LibraryImport("qwave.dll", SetLastError = true)]
    private static partial int QOSSetFlow(
        nint handle,
        uint flowId,
        int operation,
        uint size,
        void* buffer,
        uint flags,
        void* overlapped
    );
}
