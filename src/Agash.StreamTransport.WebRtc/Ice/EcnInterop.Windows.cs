using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Agash.StreamTransport.WebRtc.Ice;

// Windows: Winsock ECN, Windows 10 build 20348 (Server 2022) and later. Receiving is IP_RECVECN /
// IPV6_RECVECN on the socket and WSARecvMsg, which hands the mark over as an IP_ECN / IPV6_ECN control
// message; sending marks each datagram with an IP_ECN / IPV6_ECN control message through WSASendMsg. The
// managed Socket exposes neither. CE is the network's to set: Windows refuses to send it (WSAEINVAL).
internal static unsafe partial class EcnInterop
{
    // ws2ipdef.h: IP_ECN, IP_RECVECN, IPV6_ECN and IPV6_RECVECN are all 50.
    private const int WindowsEcnOption = 50;

    // ws2def.h / mswsock.h.
    private const uint SioGetExtensionFunctionPointer = 0xC8000006;
    private const uint SioUdpConnReset = 0x9800000C;
    private const int WsaEMsgSize = 10040;

    // WSAMSG: name@0, namelen@8, lpBuffers@16, dwBufferCount@24, Control{len@32, buf@40}, dwFlags@48.
    private const int WsaMsgSize = 56;

    // WSACMSGHDR { SIZE_T len; INT level; INT type; }, data aligned to 8: an INT's message is 20 bytes in 24.
    private const int CmsgHeader = 16;

    // WSAID_WSARECVMSG.
    private static readonly Guid WsaRecvMsgId = new(
        0xf689d7c8,
        0x6f1f,
        0x436b,
        0x8a,
        0x53,
        0xe5,
        0x4f,
        0xe3,
        0x51,
        0xc3,
        0x22
    );

    private static delegate* unmanaged[Stdcall]<nint, byte*, uint*, void*, void*, int> s_wsaRecvMsg;

    private static bool WindowsEcn { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    private static void WindowsEnableReceive(Socket socket, bool ipv6)
    {
        nint handle = socket.Handle;

        // An ICMP port-unreachable otherwise fails the next receive with WSAECONNRESET, which ICE's probing
        // of dead candidates provokes all the time.
        int off = 0;
        uint returned;
        _ = WSAIoctl(handle, SioUdpConnReset, &off, sizeof(int), null, 0, &returned, null, null);

        int on = 1;
        if (
            setsockopt(
                handle,
                ipv6 ? IPPROTO_IPV6 : IPPROTO_IP,
                WindowsEcnOption,
                (byte*)&on,
                sizeof(int)
            ) != 0
        )
        {
            throw new SocketException(Marshal.GetLastPInvokeError());
        }

        if (s_wsaRecvMsg is null)
        {
            Guid id = WsaRecvMsgId;
            void* function;
            if (
                WSAIoctl(
                    handle,
                    SioGetExtensionFunctionPointer,
                    &id,
                    (uint)sizeof(Guid),
                    &function,
                    (uint)sizeof(void*),
                    &returned,
                    null,
                    null
                ) != 0
            )
            {
                throw new SocketException(Marshal.GetLastPInvokeError());
            }

            s_wsaRecvMsg = (delegate* unmanaged[Stdcall]<
                nint,
                byte*,
                uint*,
                void*,
                void*,
                int>)function;
        }
    }

    private static int WindowsReceive(
        nint handle,
        byte[] buffer,
        bool ipv6,
        out byte ecn,
        out IPEndPoint? remote
    )
    {
        ecn = 0;
        remote = null;
        Span<byte> name = stackalloc byte[128];
        Span<byte> control = stackalloc byte[64];
        Span<byte> message = stackalloc byte[WsaMsgSize];
        Span<byte> data = stackalloc byte[16];
        message.Clear();
        fixed (byte* pBuffer = buffer)
        fixed (byte* pName = name)
        fixed (byte* pControl = control)
        fixed (byte* pData = data)
        fixed (byte* pMessage = message)
        {
            // WSABUF { ULONG len; CHAR* buf; }
            *(uint*)pData = (uint)buffer.Length;
            *(byte**)(pData + 8) = pBuffer;
            *(byte**)(pMessage + 0) = pName;
            *(int*)(pMessage + 8) = name.Length;
            *(byte**)(pMessage + 16) = pData;
            *(uint*)(pMessage + 24) = 1;
            *(uint*)(pMessage + 32) = (uint)control.Length;
            *(byte**)(pMessage + 40) = pControl;

            uint received;
            if (s_wsaRecvMsg(handle, pMessage, &received, null, null) != 0)
            {
                // A datagram larger than the buffer is dropped, as recvmsg truncation is elsewhere.
                return Marshal.GetLastSystemError() == WsaEMsgSize ? 0 : -1;
            }

            ecn = ParseWindowsEcn(control, (int)*(uint*)(pMessage + 32));
            remote = ParseSockAddr(name, ipv6);
            return (int)received;
        }
    }

    // A dual-stack socket reports IPv4 datagrams at the IPv4 level, so either level's mark is taken.
    private static byte ParseWindowsEcn(ReadOnlySpan<byte> control, int length)
    {
        int offset = 0;
        while (offset + CmsgHeader <= length)
        {
            ulong cmsgLength = *(ulong*)Ptr(control, offset);
            int level = *(int*)Ptr(control, offset + 8);
            int type = *(int*)Ptr(control, offset + 12);
            if (cmsgLength < CmsgHeader || offset + (int)cmsgLength > length)
            {
                break;
            }

            if (level is IPPROTO_IP or IPPROTO_IPV6 && type == WindowsEcnOption)
            {
                return (byte)(*(int*)Ptr(control, offset + CmsgHeader) & 0x03);
            }

            offset += Align((int)cmsgLength, 8);
        }

        return 0;
    }

    private static void WindowsSend(
        Socket socket,
        ReadOnlySpan<byte> payload,
        IPEndPoint destination,
        byte ecn
    )
    {
        SocketAddress address = destination.Serialize();
        Span<byte> name = stackalloc byte[address.Size];
        address.Buffer.Span[..address.Size].CopyTo(name);
        bool ipv6 = destination.AddressFamily == AddressFamily.InterNetworkV6;
        Span<byte> control = stackalloc byte[24];
        control.Clear();
        Span<byte> message = stackalloc byte[WsaMsgSize];
        Span<byte> data = stackalloc byte[16];
        message.Clear();
        fixed (byte* pPayload = payload)
        fixed (byte* pName = name)
        fixed (byte* pControl = control)
        fixed (byte* pData = data)
        fixed (byte* pMessage = message)
        {
            *(ulong*)pControl = CmsgHeader + sizeof(int);
            *(int*)(pControl + 8) = ipv6 ? IPPROTO_IPV6 : IPPROTO_IP;
            *(int*)(pControl + 12) = WindowsEcnOption;
            *(int*)(pControl + CmsgHeader) = ecn & 0x03;

            *(uint*)pData = (uint)payload.Length;
            *(byte**)(pData + 8) = pPayload;
            *(byte**)(pMessage + 0) = pName;
            *(int*)(pMessage + 8) = name.Length;
            *(byte**)(pMessage + 16) = pData;
            *(uint*)(pMessage + 24) = 1;
            *(uint*)(pMessage + 32) = (uint)control.Length;
            *(byte**)(pMessage + 40) = pControl;

            uint sent;
            if (WSASendMsg(socket.Handle, pMessage, 0, &sent, null, null) != 0)
            {
                throw new SocketException(Marshal.GetLastPInvokeError());
            }
        }
    }

    [LibraryImport("ws2_32", SetLastError = true)]
    private static partial int setsockopt(
        nint socket,
        int level,
        int optionName,
        byte* optionValue,
        int optionLength
    );

    [LibraryImport("ws2_32", SetLastError = true)]
    private static partial int WSAIoctl(
        nint socket,
        uint code,
        void* input,
        uint inputLength,
        void* output,
        uint outputLength,
        uint* returned,
        void* overlapped,
        void* completion
    );

    [LibraryImport("ws2_32", SetLastError = true)]
    private static partial int WSASendMsg(
        nint socket,
        byte* message,
        uint flags,
        uint* sent,
        void* overlapped,
        void* completion
    );
}
