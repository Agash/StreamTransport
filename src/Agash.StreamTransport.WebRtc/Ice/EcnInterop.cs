using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>
/// Native helpers for reading the per-packet ECN mark off a UDP socket and stamping ECT(1) on sends. The
/// managed <see cref="Socket"/> receive APIs drop the kernel's ancillary control data (<c>IPPacketInformation</c>
/// exposes only the destination address and interface - never the TOS/ECN byte), so the ECN bits in the IPv4
/// TOS / IPv6 Traffic-Class byte are only reachable through <c>recvmsg</c> with manual cmsg parsing.
///
/// <para><b>POSIX only, by design.</b> This mirrors libwebrtc's <c>PhysicalSocketServer</c>, which implements
/// ECN read/write exclusively under <c>WEBRTC_POSIX</c> and returns "not supported" for <c>OPT_RECV_ECN</c> /
/// <c>OPT_SEND_ECN</c> on Windows. Windows offers no reliable per-datagram ECN path: <c>WSASendMsg</c> with an
/// <c>IP_ECN</c> control message is rejected (WSAEINVAL) and the loopback/host stack does not surface the mark,
/// so attempting it only adds a redundant receive thread for an always-zero result. On Windows the ICE layer
/// uses the managed socket instead (Ecn=0); ECN-driven L4S congestion response is a Linux/macOS capability.</para>
///
/// <para>Per-platform struct layouts (msghdr/cmsghdr/sockaddr), option numbers and cmsg alignment differ
/// between Linux and macOS; the branches below encode each. <c>EcnLoopbackTests</c> marks a loopback datagram
/// and asserts the value is read back - a wrong layout or option number fails that test on the affected OS
/// rather than shipping. The real-socket ICE handshake tests additionally exercise source-address parsing.</para>
///
/// <para>64-bit only (all supported targets are): pointer-sized fields assume an LP64 layout.</para>
/// </summary>
internal static unsafe class EcnInterop
{
    // Protocol levels (identical across both POSIX targets and matching Windows too, though unused there).
    private const int IPPROTO_IP = 0;
    private const int IPPROTO_IPV6 = 41;

    // Linux option/cmsg numbers (<linux/in.h>, <linux/in6.h>): enable receive with IP_RECVTOS/IPV6_RECVTCLASS;
    // the kernel then delivers the value as a cmsg of type IP_TOS / IPV6_TCLASS.
    private const int LINUX_IP_TOS = 1;
    private const int LINUX_IP_RECVTOS = 13;
    private const int LINUX_IPV6_TCLASS = 67;
    private const int LINUX_IPV6_RECVTCLASS = 66;

    // macOS/Darwin option/cmsg numbers (<netinet/in.h>, <netinet6/in6.h>). Note IPV6_RECVTCLASS is 35, distinct
    // from IPV6_TCLASS (36) - Darwin deliberately diverges from FreeBSD here. BSD kernels typically deliver the
    // received TOS as a cmsg of type IP_RECVTOS, so the parser accepts either the value or the recv option.
    private const int OSX_IP_TOS = 3;
    private const int OSX_IP_RECVTOS = 27;
    private const int OSX_IPV6_TCLASS = 36;
    private const int OSX_IPV6_RECVTCLASS = 35;

    private static bool IsWindows { get; } = OperatingSystem.IsWindows();
    private static bool IsMacOS { get; } = OperatingSystem.IsMacOS();

    /// <summary>Whether the native ECN-aware receive path is usable on this OS (else callers use a managed socket).</summary>
    public static bool NativeReceiveSupported => !IsWindows;

    /// <summary>Set the outgoing ECN bits in the IPv4 TOS / IPv6 Traffic-Class field. Production uses ECT(1); tests use CE.</summary>
    internal static void SetOutgoingEcn(Socket socket, bool ipv6, byte ecn)
    {
        int value = ecn & 0x03;
        (int level, int option) = (ipv6, IsMacOS) switch
        {
            (false, true) => (IPPROTO_IP, OSX_IP_TOS),
            (false, false) => (IPPROTO_IP, LINUX_IP_TOS),
            (true, true) => (IPPROTO_IPV6, OSX_IPV6_TCLASS),
            (true, false) => (IPPROTO_IPV6, LINUX_IPV6_TCLASS),
        };
        NativeSetSocketOption(socket.Handle, level, option, value);
    }

    /// <summary>Best-effort: stamp ECT(1) on every outgoing datagram so a bottleneck marks CE instead of dropping.</summary>
    public static void EnableEct1OnSend(Socket socket, bool ipv6)
    {
        const byte ect1 = 0x01;
        try
        {
            SetOutgoingEcn(socket, ipv6, ect1);
        }
        catch (SocketException)
        {
            // OS refused the mark (policy/privilege/platform). The loop still runs, just without outbound ECT.
        }
    }

    /// <summary>Best-effort: ask the kernel to attach the received TOS/ECN byte as ancillary control data.</summary>
    public static void EnableEcnReceive(Socket socket, bool ipv6)
    {
        try
        {
            (int level, int option) = (ipv6, IsMacOS) switch
            {
                (false, true) => (IPPROTO_IP, OSX_IP_RECVTOS),
                (false, false) => (IPPROTO_IP, LINUX_IP_RECVTOS),
                (true, true) => (IPPROTO_IPV6, OSX_IPV6_RECVTCLASS),
                (true, false) => (IPPROTO_IPV6, LINUX_IPV6_RECVTCLASS),
            };
            NativeSetSocketOption(socket.Handle, level, option, 1);
        }
        catch (SocketException)
        {
        }
    }

    private static void NativeSetSocketOption(nint handle, int level, int option, int value)
    {
        // The managed Socket.SetSocketOption validates option numbers against a known set and rejects the raw
        // IP_RECVTOS / IPV6_RECVTCLASS values with EINVAL even when the kernel accepts them, so we go to setsockopt
        // directly.
        int rc = unix_setsockopt((int)handle, level, option, (byte*)&value, sizeof(int));
        if (rc != 0)
        {
            throw new SocketException(Marshal.GetLastPInvokeError());
        }
    }

    [DllImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    private static extern int unix_setsockopt(int socket, int level, int optionName, byte* optionValue, uint optionLength);

    /// <summary>
    /// Blocking receive of one datagram, returning the byte count, the 2-bit ECN mark and the source endpoint.
    /// Returns -1 (with <paramref name="remote"/> null) on error - e.g. the socket was closed during shutdown.
    /// </summary>
    public static int Receive(nint handle, byte[] buffer, bool ipv6, out byte ecn, out IPEndPoint? remote)
        => UnixReceive((int)handle, buffer, ipv6, out ecn, out remote);

    // ---- Unix (Linux + macOS): recvmsg ----

    [DllImport("libc", SetLastError = true)]
    private static extern nint recvmsg(int sockfd, byte* msg, int flags);

    private static int UnixReceive(int fd, byte[] buffer, bool ipv6, out byte ecn, out IPEndPoint? remote)
    {
        ecn = 0;
        remote = null;

        Span<byte> name = stackalloc byte[128];
        Span<byte> control = stackalloc byte[128];
        Span<byte> hdr = stackalloc byte[64];   // msghdr (56 bytes used) zero-filled
        Span<byte> iov = stackalloc byte[16];   // iovec
        hdr.Clear();

        fixed (byte* pBuf = buffer)
        fixed (byte* pName = name)
        fixed (byte* pControl = control)
        fixed (byte* pIov = iov)
        fixed (byte* pHdr = hdr)
        {
            // iovec { void* base; size_t len }
            *(nint*)(pIov + 0) = (nint)pBuf;
            *(nuint*)(pIov + 8) = (nuint)buffer.Length;

            // msghdr layout: name@0, namelen@8, iov@16, iovlen@24, control@32, controllen@40, flags@48.
            // iovlen/controllen are size_t on Linux (8 bytes) but int/socklen_t (4 bytes) on macOS.
            *(nint*)(pHdr + 0) = (nint)pName;
            *(uint*)(pHdr + 8) = (uint)name.Length;
            *(nint*)(pHdr + 16) = (nint)pIov;
            *(nint*)(pHdr + 32) = (nint)pControl;
            if (IsMacOS)
            {
                *(int*)(pHdr + 24) = 1;
                *(uint*)(pHdr + 40) = (uint)control.Length;
            }
            else
            {
                *(nuint*)(pHdr + 24) = 1;
                *(nuint*)(pHdr + 40) = (nuint)control.Length;
            }

            nint n = recvmsg(fd, pHdr, 0);
            if (n < 0)
            {
                return -1;
            }

            ulong controlLen = IsMacOS ? *(uint*)(pHdr + 40) : (ulong)*(nuint*)(pHdr + 40);
            ecn = ParseUnixEcn(control, (int)controlLen, ipv6);
            remote = ParseSockAddr(name, ipv6);
            return (int)n;
        }
    }

    private static byte ParseUnixEcn(ReadOnlySpan<byte> control, int controlLen, bool ipv6)
    {
        // cmsghdr: Linux { size_t len; int level; int type } -> data@16, align 8.
        //          macOS { uint   len; int level; int type } -> data@12, align 4.
        int headerSize = IsMacOS ? 12 : 16;
        int align = IsMacOS ? 4 : 8;
        int wantLevel = ipv6 ? IPPROTO_IPV6 : IPPROTO_IP;

        int offset = 0;
        while (offset + headerSize <= controlLen)
        {
            ulong cmsgLen = IsMacOS ? *(uint*)Ptr(control, offset) : (ulong)*(nuint*)Ptr(control, offset);
            int level = *(int*)Ptr(control, offset + (IsMacOS ? 4 : 8));
            int type = *(int*)Ptr(control, offset + (IsMacOS ? 8 : 12));
            if (cmsgLen < (ulong)headerSize || offset + (int)cmsgLen > controlLen)
            {
                break;
            }

            if (level == wantLevel && IsTosCmsg(type, ipv6))
            {
                // Data is the TOS/Traffic-Class byte (delivered as a byte or an int; either way the byte we want
                // is the first data byte). The ECN codepoint is its low 2 bits (RFC 3168 §5).
                return (byte)(control[offset + headerSize] & 0x03);
            }

            offset += Align((int)cmsgLen, align);
        }

        return 0;
    }

    // Accept either the value option (IP_TOS / IPV6_TCLASS - how Linux delivers) or the recv option
    // (IP_RECVTOS / IPV6_RECVTCLASS - how BSD/macOS delivers). We only enabled the recv option, so the TOS/TCLASS
    // is the only ancillary datum at this protocol level either way.
    private static bool IsTosCmsg(int type, bool ipv6)
    {
        if (IsMacOS)
        {
            return ipv6
                ? type is OSX_IPV6_TCLASS or OSX_IPV6_RECVTCLASS
                : type is OSX_IP_TOS or OSX_IP_RECVTOS;
        }

        return ipv6
            ? type is LINUX_IPV6_TCLASS or LINUX_IPV6_RECVTCLASS
            : type is LINUX_IP_TOS or LINUX_IP_RECVTOS;
    }

    // ---- shared helpers ----

    private static IPEndPoint ParseSockAddr(ReadOnlySpan<byte> sa, bool ipv6)
    {
        // Family value differs per OS for AF_INET6, so we key the layout off the known socket family instead.
        // sockaddr_in:  [family][port BE @2][addr @4]; sockaddr_in6: [..][port BE @2][flow @4][addr @8][scope @24].
        // On macOS byte 0 is sa_len and byte 1 is the family, but port/address offsets are identical, so we read
        // the port/address by fixed offset and never trust the family byte.
        ushort port = (ushort)((sa[2] << 8) | sa[3]);
        if (!ipv6)
        {
            var v4 = new IPAddress(sa.Slice(4, 4));
            return new IPEndPoint(v4, port);
        }

        var v6 = new IPAddress(sa.Slice(8, 16));
        return new IPEndPoint(v6, port);
    }

    private static byte* Ptr(ReadOnlySpan<byte> span, int offset)
        => (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span)) + offset;

    private static int Align(int len, int align) => (len + (align - 1)) & ~(align - 1);
}
