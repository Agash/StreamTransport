using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Agash.StreamTransport.Linux.V4l2;

// The V4L2 uAPI (linux/videodev2.h) as the 64-bit kernel lays it out, and the libc calls that drive it.
internal static unsafe partial class V4l2Native
{
    public const uint BufTypeVideoCapture = 1;
    public const uint BufTypeVideoCaptureMplane = 9;
    public const uint BufTypeVideoOutput = 2;
    public const uint MemoryMmap = 1;

    public const uint CapVideoCapture = 0x0000_0001;
    public const uint CapVideoCaptureMplane = 0x0000_1000;
    public const uint CapVideoOutput = 0x0000_0002;
    public const uint CapStreaming = 0x0400_0000;
    public const uint CapDeviceCaps = 0x8000_0000;

    public const uint FmtFlagCompressed = 0x1;
    public const uint FrmSizeDiscrete = 1;
    public const uint FrmIvalDiscrete = 1;

    public const uint BufFlagTimestampMask = 0xE000;
    public const uint BufFlagTimestampMonotonic = 0x2000;
    public const uint BufFlagError = 0x0040;

    public const int ORdWr = 2;
    public const int ONonBlock = 0x800;
    public const int OCloExec = 0x80000;
    public const int ProtRead = 1;
    public const int MapShared = 1;
    public const short PollIn = 1;
    public const int EAgain = 11;
    public const int EInvalid = 22;
    public const int EIntr = 4;

    public static readonly nuint QueryCap = Ior(0, sizeof(Capability));
    public static readonly nuint EnumFmt = Iowr(2, sizeof(FmtDesc));
    public static readonly nuint GetFmt = Iowr(4, sizeof(Format));
    public static readonly nuint SetFmt = Iowr(5, sizeof(Format));
    public static readonly nuint ReqBufs = Iowr(8, sizeof(RequestBuffers));
    public static readonly nuint QueryBuf = Iowr(9, sizeof(V4l2Buffer));
    public static readonly nuint QBuf = Iowr(15, sizeof(V4l2Buffer));
    public static readonly nuint ExpBuf = Iowr(16, sizeof(ExportBuffer));
    public static readonly nuint DqBuf = Iowr(17, sizeof(V4l2Buffer));
    public static readonly nuint StreamOn = Iow(18, sizeof(int));
    public static readonly nuint StreamOff = Iow(19, sizeof(int));
    public static readonly nuint GetParm = Iowr(21, sizeof(StreamParm));
    public static readonly nuint SetParm = Iowr(22, sizeof(StreamParm));
    public static readonly nuint EnumFrameSizes = Iowr(74, sizeof(FrmSizeEnum));
    public static readonly nuint EnumFrameIntervals = Iowr(75, sizeof(FrmIvalEnum));

    public static uint FourCc(string code) =>
        (uint)(code[0] | (code[1] << 8) | (code[2] << 16) | (code[3] << 24));

    public static string FourCcName(uint code) =>
        string.Create(4, code, static (span, c) =>
        {
            for (int i = 0; i < 4; i++)
            {
                span[i] = (char)((c >> (8 * i)) & 0xFF);
            }
        });

    public static string Text(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }

    // An ioctl that retries when a signal interrupts it.
    public static int Control<T>(int fd, nuint request, ref T argument)
        where T : unmanaged
    {
        int result;
        fixed (T* pointer = &argument)
        {
            do
            {
                result = Ioctl(fd, request, pointer);
            } while (result < 0 && Marshal.GetLastPInvokeError() == EIntr);
        }

        return result;
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(int fd, nuint request, void* argument);

    [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
    public static partial nint Mmap(nint address, nuint length, int protection, int flags, int fd, long offset);

    [LibraryImport("libc", EntryPoint = "munmap", SetLastError = true)]
    public static partial int Munmap(nint address, nuint length);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    public static partial int Poll(PollFd* fds, nuint count, int timeout);

    [LibraryImport("libc", EntryPoint = "eventfd", SetLastError = true)]
    public static partial int EventFd(uint initial, int flags);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    public static partial nint Write(int fd, void* buffer, nuint count);

    private static nuint Ior(uint nr, int size) => Ioc(2, nr, size);

    private static nuint Iow(uint nr, int size) => Ioc(1, nr, size);

    private static nuint Iowr(uint nr, int size) => Ioc(3, nr, size);

    private static nuint Ioc(uint direction, uint nr, int size) =>
        (direction << 30) | ((uint)size << 16) | ((uint)'V' << 8) | nr;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short ReturnedEvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Capability
    {
        public fixed byte Driver[16];
        public fixed byte Card[32];
        public fixed byte BusInfo[32];
        public uint Version;
        public uint Capabilities;
        public uint DeviceCaps;
        public fixed uint Reserved[3];

        public readonly uint Effective =>
            (Capabilities & CapDeviceCaps) != 0 ? DeviceCaps : Capabilities;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FmtDesc
    {
        public uint Index;
        public uint Type;
        public uint Flags;
        public fixed byte Description[32];
        public uint PixelFormat;
        public uint MbusCode;
        public fixed uint Reserved[3];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PixFormat
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public uint Field;
        public uint BytesPerLine;
        public uint SizeImage;
        public uint Colorspace;
        public uint Private;
        public uint Flags;
        public uint YcbcrEncoding;
        public uint Quantization;
        public uint TransferFunction;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PlanePixFormat
    {
        public uint SizeImage;
        public uint BytesPerLine;
        public fixed ushort Reserved[6];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PixFormatMplane
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public uint Field;
        public uint Colorspace;
        public PlanePixFormat Plane0;
        public PlanePixFormat Plane1;
        public PlanePixFormat Plane2;
        public PlanePixFormat Plane3;
        public PlanePixFormat Plane4;
        public PlanePixFormat Plane5;
        public PlanePixFormat Plane6;
        public PlanePixFormat Plane7;
        public byte PlaneCount;
        public byte Flags;
        public byte YcbcrEncoding;
        public byte Quantization;
        public byte TransferFunction;
        public fixed byte Reserved[7];
    }

    [StructLayout(LayoutKind.Explicit, Size = 208)]
    public struct Format
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public PixFormat Pix;

        [FieldOffset(8)]
        public PixFormatMplane PixMp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RequestBuffers
    {
        public uint Count;
        public uint Type;
        public uint Memory;
        public uint Capabilities;
        public byte Flags;
        public fixed byte Reserved[3];
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct Plane
    {
        [FieldOffset(0)]
        public uint BytesUsed;

        [FieldOffset(4)]
        public uint Length;

        [FieldOffset(8)]
        public uint MemOffset;

        [FieldOffset(16)]
        public uint DataOffset;
    }

    [StructLayout(LayoutKind.Explicit, Size = 88)]
    public struct V4l2Buffer
    {
        [FieldOffset(0)]
        public uint Index;

        [FieldOffset(4)]
        public uint Type;

        [FieldOffset(8)]
        public uint BytesUsed;

        [FieldOffset(12)]
        public uint Flags;

        [FieldOffset(16)]
        public uint Field;

        [FieldOffset(24)]
        public long TimestampSeconds;

        [FieldOffset(32)]
        public long TimestampMicroseconds;

        [FieldOffset(56)]
        public uint Sequence;

        [FieldOffset(60)]
        public uint Memory;

        [FieldOffset(64)]
        public uint Offset;

        [FieldOffset(64)]
        public Plane* Planes;

        [FieldOffset(72)]
        public uint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ExportBuffer
    {
        public uint Type;
        public uint Index;
        public uint Plane;
        public uint Flags;
        public int Fd;
        public fixed uint Reserved[11];
    }

    [StructLayout(LayoutKind.Explicit, Size = 204)]
    public struct StreamParm
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(4)]
        public uint Capability;

        [FieldOffset(8)]
        public uint CaptureMode;

        [FieldOffset(12)]
        public uint TimePerFrameNumerator;

        [FieldOffset(16)]
        public uint TimePerFrameDenominator;
    }

    [StructLayout(LayoutKind.Explicit, Size = 44)]
    public struct FrmSizeEnum
    {
        [FieldOffset(0)]
        public uint Index;

        [FieldOffset(4)]
        public uint PixelFormat;

        [FieldOffset(8)]
        public uint Type;

        [FieldOffset(12)]
        public uint Width;

        [FieldOffset(16)]
        public uint Height;

        // Stepwise: min width at 12, max width 16, step width 20, min height 24, max height 28, step 32.
        [FieldOffset(16)]
        public uint MaxWidth;

        [FieldOffset(28)]
        public uint MaxHeight;
    }

    [StructLayout(LayoutKind.Explicit, Size = 52)]
    public struct FrmIvalEnum
    {
        [FieldOffset(0)]
        public uint Index;

        [FieldOffset(4)]
        public uint PixelFormat;

        [FieldOffset(8)]
        public uint Width;

        [FieldOffset(12)]
        public uint Height;

        [FieldOffset(16)]
        public uint Type;

        [FieldOffset(20)]
        public uint Numerator;

        [FieldOffset(24)]
        public uint Denominator;
    }

    // The layouts the kernel expects, checked by the tests.
    internal static ReadOnlySpan<(string Name, int Size, int Expected)> Layouts =>
        new (string, int, int)[]
        {
            (nameof(Capability), Unsafe.SizeOf<Capability>(), 104),
            (nameof(FmtDesc), Unsafe.SizeOf<FmtDesc>(), 64),
            (nameof(PixFormat), Unsafe.SizeOf<PixFormat>(), 48),
            (nameof(PixFormatMplane), Unsafe.SizeOf<PixFormatMplane>(), 192),
            (nameof(Format), Unsafe.SizeOf<Format>(), 208),
            (nameof(RequestBuffers), Unsafe.SizeOf<RequestBuffers>(), 20),
            (nameof(Plane), Unsafe.SizeOf<Plane>(), 64),
            (nameof(V4l2Buffer), Unsafe.SizeOf<V4l2Buffer>(), 88),
            (nameof(ExportBuffer), Unsafe.SizeOf<ExportBuffer>(), 64),
            (nameof(StreamParm), Unsafe.SizeOf<StreamParm>(), 204),
            (nameof(FrmSizeEnum), Unsafe.SizeOf<FrmSizeEnum>(), 44),
            (nameof(FrmIvalEnum), Unsafe.SizeOf<FrmIvalEnum>(), 52),
        };
}
