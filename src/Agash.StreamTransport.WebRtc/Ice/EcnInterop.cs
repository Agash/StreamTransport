using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>
/// Native helpers for reading the per-packet ECN mark off a UDP socket and marking each sent datagram. The
/// managed <see cref="Socket"/> API drops the ancillary control data the mark arrives in
/// (<c>IPPacketInformation</c> carries only the destination address and interface) and cannot attach any on
/// send, so each platform's native call is used: <c>recvmsg</c> and <c>sendmsg</c> with an <c>IP_TOS</c> /
/// <c>IPV6_TCLASS</c> control message on Linux and macOS; <c>WSARecvMsg</c> and <c>WSASendMsg</c> with
/// <c>IP_ECN</c> / <c>IPV6_ECN</c> on Windows 10 build 20348 and later (draft-ietf-tsvwg-udp-ecn-08
/// sections 3 and 4.2). Marking per datagram lets media carry ECT while STUN, DTLS and RTCP do not
/// (RFC 6679 section 7.3.1).
///
/// <para>Per-platform struct layouts (msghdr/WSAMSG, cmsghdr/WSACMSGHDR, sockaddr), option numbers and
/// control-message alignment differ; the branches encode each. <c>EcnLoopbackTests</c> marks a loopback
/// datagram and asserts the mark is read back, so a wrong layout or option number fails on the affected OS.</para>
///
/// <para>64-bit only (all supported targets are): pointer-sized fields assume an LP64 / LLP64 layout.</para>
/// </summary>
internal static unsafe partial class EcnInterop
{
    // Protocol levels, the same on every target.
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

    /// <summary>Whether the native ECN-aware path is usable on this OS (else callers use a managed socket).</summary>
    public static bool NativeReceiveSupported => !IsWindows || WindowsEcn;

    /// <summary>Whether datagrams can be marked one by one on this OS.</summary>
    public static bool NativeSendSupported => !IsWindows || WindowsEcn;

    /// <summary>Sends one datagram carrying an ECN codepoint; a not-ECT one is a plain send.</summary>
    /// <param name="socket">The socket, in blocking mode.</param>
    /// <param name="payload">The datagram.</param>
    /// <param name="destination">Where it goes.</param>
    /// <param name="ecn">The two ECN bits.</param>
    /// <exception cref="SocketException">The send failed.</exception>
    internal static void Send(
        Socket socket,
        ReadOnlySpan<byte> payload,
        IPEndPoint destination,
        byte ecn
    )
    {
        if ((ecn & 0x03) == 0 || !NativeSendSupported)
        {
            _ = socket.SendTo(payload, SocketFlags.None, destination);
        }
        else if (IsWindows)
        {
            WindowsSend(socket, payload, destination, ecn);
        }
        else
        {
            UnixSend(
                (int)socket.Handle,
                payload,
                destination,
                socket.AddressFamily == AddressFamily.InterNetworkV6,
                ecn
            );
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
            if (IsWindows)
            {
                WindowsEnableReceive(socket, ipv6);
            }
            else
            {
                NativeSetSocketOption(socket.Handle, level, option, 1);
            }
        }
        catch (SocketException)
        {
            // Deliberately not logged: a stack without the option delivers no ECN marks, which the
            // receive path already reads as not ECN-capable; it would log once for every socket.
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
    private static extern int unix_setsockopt(
        int socket,
        int level,
        int optionName,
        byte* optionValue,
        uint optionLength
    );

    /// <summary>
    /// Blocking receive of one datagram, returning the byte count, the 2-bit ECN mark and the source endpoint.
    /// Returns -1 (with <paramref name="remote"/> null) on error - e.g. the socket was closed during shutdown.
    /// </summary>
    public static int Receive(
        nint handle,
        byte[] buffer,
        bool ipv6,
        out byte ecn,
        out IPEndPoint? remote
    ) =>
        IsWindows
            ? WindowsReceive(handle, buffer, ipv6, out ecn, out remote)
            : UnixReceive((int)handle, buffer, ipv6, out ecn, out remote);

    // ---- Unix (Linux + macOS): recvmsg, sendmsg ----

    [DllImport("libc", SetLastError = true)]
    private static extern nint recvmsg(int sockfd, byte* msg, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern nint sendmsg(int sockfd, byte* msg, int flags);

    // One datagram with the codepoint in an IP_TOS (IPv4) or IPV6_TCLASS (IPv6) control message, an int on
    // both Linux and macOS (draft-ietf-tsvwg-udp-ecn-08 section 4.2.1).
    private static void UnixSend(
        int fd,
        ReadOnlySpan<byte> payload,
        IPEndPoint destination,
        bool ipv6Socket,
        byte ecn
    )
    {
        Span<byte> name = stackalloc byte[28];
        int nameLength = WriteSockAddr(name, destination, ipv6Socket);
        int headerSize = IsMacOS ? 12 : 16;
        int controlLength = Align(headerSize + sizeof(int), IsMacOS ? 4 : 8);
        Span<byte> control = stackalloc byte[controlLength];
        control.Clear();
        Span<byte> hdr = stackalloc byte[64];
        hdr.Clear();
        Span<byte> iov = stackalloc byte[16];
        (int level, int type) = (ipv6Socket, IsMacOS) switch
        {
            (false, true) => (IPPROTO_IP, OSX_IP_TOS),
            (false, false) => (IPPROTO_IP, LINUX_IP_TOS),
            (true, true) => (IPPROTO_IPV6, OSX_IPV6_TCLASS),
            (true, false) => (IPPROTO_IPV6, LINUX_IPV6_TCLASS),
        };

        fixed (byte* pPayload = payload)
        fixed (byte* pName = name)
        fixed (byte* pControl = control)
        fixed (byte* pIov = iov)
        fixed (byte* pHdr = hdr)
        {
            *(nint*)(pIov + 0) = (nint)pPayload;
            *(nuint*)(pIov + 8) = (nuint)payload.Length;

            // cmsghdr: Linux { size_t len; int level; int type } then data at 16; macOS { uint len; ... } at 12.
            if (IsMacOS)
            {
                *(uint*)pControl = (uint)(headerSize + sizeof(int));
                *(int*)(pControl + 4) = level;
                *(int*)(pControl + 8) = type;
            }
            else
            {
                *(nuint*)pControl = (nuint)(headerSize + sizeof(int));
                *(int*)(pControl + 8) = level;
                *(int*)(pControl + 12) = type;
            }

            *(int*)(pControl + headerSize) = ecn & 0x03;

            // msghdr: name@0, namelen@8, iov@16, iovlen@24, control@32, controllen@40, flags@48.
            *(nint*)(pHdr + 0) = (nint)pName;
            *(uint*)(pHdr + 8) = (uint)nameLength;
            *(nint*)(pHdr + 16) = (nint)pIov;
            *(nint*)(pHdr + 32) = (nint)pControl;
            if (IsMacOS)
            {
                *(int*)(pHdr + 24) = 1;
                *(uint*)(pHdr + 40) = (uint)controlLength;
            }
            else
            {
                *(nuint*)(pHdr + 24) = 1;
                *(nuint*)(pHdr + 40) = (nuint)controlLength;
            }

            if (sendmsg(fd, pHdr, 0) < 0)
            {
                throw new SocketException(Marshal.GetLastPInvokeError());
            }
        }
    }

    // A native sockaddr_in or sockaddr_in6; an IPv4 destination on an IPv6 socket is IPv4-mapped. macOS has a
    // length byte before a one-byte family; Linux a two-byte family.
    private static int WriteSockAddr(Span<byte> sa, IPEndPoint destination, bool ipv6Socket)
    {
        sa.Clear();
        IPAddress address = ipv6Socket ? destination.Address.MapToIPv6() : destination.Address;
        int length = ipv6Socket ? 28 : 16;
        int family = ipv6Socket ? (IsMacOS ? 30 : 10) : 2;
        if (IsMacOS)
        {
            sa[0] = (byte)length;
            sa[1] = (byte)family;
        }
        else
        {
            sa[0] = (byte)family;
            sa[1] = (byte)(family >> 8);
        }

        sa[2] = (byte)(destination.Port >> 8);
        sa[3] = (byte)destination.Port;
        if (ipv6Socket)
        {
            _ = address.TryWriteBytes(sa.Slice(8, 16), out _);
            uint scope = (uint)address.ScopeId;
            sa[24] = (byte)scope;
            sa[25] = (byte)(scope >> 8);
            sa[26] = (byte)(scope >> 16);
            sa[27] = (byte)(scope >> 24);
        }
        else
        {
            _ = address.TryWriteBytes(sa.Slice(4, 4), out _);
        }

        return length;
    }

    private static int UnixReceive(
        int fd,
        byte[] buffer,
        bool ipv6,
        out byte ecn,
        out IPEndPoint? remote
    )
    {
        ecn = 0;
        remote = null;

        Span<byte> name = stackalloc byte[128];
        Span<byte> control = stackalloc byte[128];
        Span<byte> hdr = stackalloc byte[64]; // msghdr (56 bytes used) zero-filled
        Span<byte> iov = stackalloc byte[16]; // iovec
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
            ulong cmsgLen = IsMacOS
                ? *(uint*)Ptr(control, offset)
                : (ulong)*(nuint*)Ptr(control, offset);
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

    private static byte* Ptr(ReadOnlySpan<byte> span, int offset) =>
        (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span)) + offset;

    private static int Align(int len, int align) => (len + (align - 1)) & ~(align - 1);
}
