#if HAS_SYPHON
using System.Runtime.Versioning;
using Agash.StreamTransport.Codecs;
using CoreVideo;
using IOSurface;
using ObjCRuntime;
using Syphon.NET;

namespace StreamTransport.Agent;

/// <summary>
/// A standalone check (no external Syphon app needed) of the macOS zero-copy path: it builds a neutral-grey
/// BGRA IOSurface, encodes it with VideoToolbox, decodes it back, confirms the decoder yields an NV12
/// IOSurface, then publishes it and captures it through a Syphon loopback client to prove the publish/capture
/// wiring. It also exercises the Metal alpha pack/unpack and the opaque NV12->BGRA convert. Surfaces are the
/// Microsoft <see cref="IOSurface"/> bindings directly. Run with <c>selftest</c>. macOS-only.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class SyphonSelfTest
{
    // Callback-shaped locked byte access for the selftest's fill/verify loops. The lock cycle and the
    // base-address span come from Syphon.NET's IOSurfaceExtensions.LockBytes; this only adapts it to a
    // delegate so the loops below can share one lock/unlock.
    private delegate void SurfaceBytes(Span<byte> bytes, int stride);

    private static void WithLockedBytes(IOSurface.IOSurface surface, bool readOnly, SurfaceBytes body)
    {
        using IOSurfaceExtensions.LockedSurface locked = surface.LockBytes(readOnly);
        body(locked.Bytes, locked.BytesPerRow);
    }

    private static IOSurface.IOSurface Wrap(nint handle) => Runtime.GetINativeObject<IOSurface.IOSurface>(handle, owns: false)!;

    public static int Run()
    {
        FFmpegLibrary.EnsureLoaded();

        const int width = 256;
        const int height = 256;

        using var server = new SyphonServer("StreamTransport SelfTest");

        // 1) A neutral-grey BGRA source surface, owned by the Syphon server.
        IOSurface.IOSurface source = server.AcquireSurface(width, height, CVPixelFormatType.CV32BGRA);
        FillGreyBgra(source);
        Console.WriteLine($"source IOSurface {source.Width}x{source.Height}, format={(uint)source.PixelFormat:x}.");

        // 2) Encode the IOSurface with VideoToolbox, then 3) decode it back.
        using var encoder = new VideoToolboxVideoEncoder(width, height, fps: 30, bitrate: 4_000_000);
        using var decoder = new VideoToolboxVideoDecoder();

        bool decoded = false;
        int decodedWidth = 0, decodedHeight = 0;
        nint decodedSurface = 0;
        int encodedBytes = 0;
        for (int i = 0; i < 30 && !decoded; i++)
        {
            byte[]? accessUnit = encoder.EncodeIOSurface(source.Handle.Handle);
            if (accessUnit is null)
            {
                continue;
            }

            encodedBytes = accessUnit.Length;
            if (decoder.Decode(accessUnit, 0, out decodedWidth, out decodedHeight, out _))
            {
                decoded = true;
                decodedSurface = decoder.OutputIOSurface;
            }
        }

        if (!decoded || decodedSurface == 0)
        {
            Console.WriteLine("VT-ROUNDTRIP-FAIL: VideoToolbox encode/decode produced no surface.");
            return 1;
        }

        IOSurface.IOSurface decodedView = Wrap(decodedSurface);
        Console.WriteLine(
            $"VT round-trip OK: {encodedBytes}-byte HEVC AU -> {decodedWidth}x{decodedHeight} IOSurface, " +
            $"format={(uint)decodedView.PixelFormat:x} (NV12-family VideoToolbox surface; a colour-correct OBS " +
            "publish would need an NV12->BGRA Metal pass).");

        // 4) Publish the decoded surface and capture it back through a Syphon loopback client.
        bool looped = false;
        using (SyphonClient client = server.CreateLoopbackClient())
        {
            for (int i = 0; i < 100 && !looped; i++)
            {
                server.Publish(decodedView);
                SyphonServer.PumpEvents(TimeSpan.FromMilliseconds(5));
                using IOSurface.IOSurface? frame = client.TryGetFrame();
                if (frame is { } f)
                {
                    looped = true;
                    Console.WriteLine($"loopback frame received: {f.Width}x{f.Height}, format={(uint)f.PixelFormat:x}.");
                }
            }
        }

        Console.WriteLine(looped ? "SYPHON-LOOPBACK-OK" : "SYPHON-LOOPBACK-FAIL");

        // Exercise the GPU alpha path (Metal pack/unpack) and the opaque NV12->BGRA publish path (the M-1
        // colour regression). Run both unconditionally so all diagnostics print, then combine.
        bool alphaOk = RunAlpha();
        bool opaqueOk = RunOpaque();

        // Success = the full zero-copy GPU path ran: VideoToolbox encode+decode round-trip, a Syphon
        // publish/capture round-trip, the Metal alpha pack/unpack, and the opaque NV12->BGRA convert -
        // all over IOSurface, no CPU readback.
        bool ok = looped && alphaOk && opaqueOk;
        Console.WriteLine(ok ? "SELFTEST-OK" : "SELFTEST-FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// M-1 regression: the opaque (non-alpha) receive path. A hardware decoder yields NV12; publishing it
    /// as if BGRA is the wrong colour. This drives known solid colours through
    /// BGRA -> VideoToolbox encode -> decode (NV12) -> <see cref="MetalNv12ToBgraConverter"/> -> readback and
    /// asserts each colour survives, so a regression (NV12 republished raw, or a black/plane bug) is caught.
    /// </summary>
    private static bool RunOpaque()
    {
        const int width = 256;
        const int height = 256;

        // Four solid colour bands (BGRA, opaque). Mid-range values so the BT.709 limited-range round-trip
        // does not clip at the extremes.
        (byte B, byte G, byte R)[] bands = [(40, 40, 200), (40, 200, 40), (200, 40, 40), (180, 180, 180)];

        using var server = new SyphonServer("StreamTransport Opaque SelfTest");
        IOSurface.IOSurface input = server.AcquireSurface(width, height, CVPixelFormatType.CV32BGRA);
        FillColourBandsBgra(input, width, height, bands);

        using var encoder = new VideoToolboxVideoEncoder(width, height, fps: 30, bitrate: 12_000_000);
        using var decoder = new VideoToolboxVideoDecoder();
        using var converter = new MetalNv12ToBgraConverter();

        bool decoded = false;
        nint decodedSurface = 0;
        for (int i = 0; i < 30 && !decoded; i++)
        {
            byte[]? accessUnit = encoder.EncodeIOSurface(input.Handle.Handle);
            if (accessUnit is null)
            {
                continue;
            }

            if (decoder.Decode(accessUnit, 0, out _, out _, out _))
            {
                decoded = true;
                decodedSurface = decoder.OutputIOSurface;
            }
        }

        if (!decoded || decodedSurface == 0)
        {
            Console.WriteLine("OPAQUE-FAIL: VideoToolbox produced no decoded surface.");
            return false;
        }

        IOSurface.IOSurface decodedView = Wrap(decodedSurface);
        if (decodedView.IsBgra())
        {
            Console.WriteLine("OPAQUE-FAIL: decoder yielded BGRA (expected NV12) - the conversion path is untested.");
            return false;
        }

        IOSurface.IOSurface bgra = converter.Convert(decodedView);
        if ((int)bgra.Width != width || (int)bgra.Height != height)
        {
            Console.WriteLine($"OPAQUE-FAIL: converted {bgra.Width}x{bgra.Height}, expected {width}x{height}.");
            return false;
        }

        int maxColourError = 0;
        WithLockedBytes(bgra, readOnly: true, (bytes, stride) =>
        {
            for (int b = 0; b < bands.Length; b++)
            {
                int y = (b * height / bands.Length) + (height / bands.Length / 2);
                int p = (y * stride) + ((width / 2) * 4);
                maxColourError = Math.Max(maxColourError, Math.Abs(bytes[p] - bands[b].B));
                maxColourError = Math.Max(maxColourError, Math.Abs(bytes[p + 1] - bands[b].G));
                maxColourError = Math.Max(maxColourError, Math.Abs(bytes[p + 2] - bands[b].R));
            }
        });

        const int tolerance = 24; // BT.709 limited round-trip + HEVC on flat colour is well within this.
        bool opaqueOk = maxColourError <= tolerance;
        Console.WriteLine(opaqueOk
            ? $"OPAQUE-OK: NV12->BGRA colour preserved through encode/decode (max error {maxColourError} <= {tolerance})."
            : $"OPAQUE-FAIL: max colour error {maxColourError} > {tolerance} (NV12 likely published as BGRA - M-1).");
        return opaqueOk;
    }

    /// <summary>Fill a BGRA IOSurface with horizontal solid-colour bands (opaque).</summary>
    private static void FillColourBandsBgra(IOSurface.IOSurface surface, int width, int height, (byte B, byte G, byte R)[] bands) =>
        WithLockedBytes(surface, readOnly: false, (bytes, stride) =>
        {
            for (int y = 0; y < height; y++)
            {
                (byte B, byte G, byte R) c = bands[Math.Min(y * bands.Length / height, bands.Length - 1)];
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int p = row + (x * 4);
                    bytes[p] = c.B;
                    bytes[p + 1] = c.G;
                    bytes[p + 2] = c.R;
                    bytes[p + 3] = 255;
                }
            }
        });

    /// <summary>
    /// Verifies the macOS GPU alpha path with no external app: (A) Metal pack of a known BGRA surface
    /// is byte-exact (left = colour, right = alpha-as-grey), then (B) a full round-trip
    /// pack -> VideoToolbox encode -> decode -> Metal unpack restores the alpha channel. All zero-copy
    /// over IOSurface.
    /// </summary>
    private static bool RunAlpha()
    {
        const int width = 256;
        const int height = 256;

        // Four horizontal bands, each a solid colour with a distinct alpha (opaque -> transparent).
        (byte B, byte G, byte R, byte A)[] bands =
        [
            (200, 30, 30, 255),
            (30, 200, 30, 170),
            (30, 30, 200, 85),
            (128, 128, 128, 0),
        ];

        using var server = new SyphonServer("StreamTransport Alpha SelfTest");
        IOSurface.IOSurface input = server.AcquireSurface(width, height, CVPixelFormatType.CV32BGRA);
        FillBandsBgra(input, width, height, bands);

        using var codec = new MetalAlphaCodec();

        // (A) Pack and read back the 2W x H surface; check the layout byte-exact (no codec involved).
        IOSurface.IOSurface packed = codec.Pack(input);
        if ((int)packed.Width != width * 2 || (int)packed.Height != height)
        {
            Console.WriteLine($"ALPHA-PACK-FAIL: expected {width * 2}x{height}, got {packed.Width}x{packed.Height}.");
            return false;
        }

        if (!VerifyPackedLayout(packed, width, height, bands))
        {
            return false;
        }

        Console.WriteLine("ALPHA-PACK-OK: colour|alpha laid out byte-exact in a 2W x H BGRA surface.");

        // (B) Encode the packed surface with VideoToolbox, decode it, and GPU-unpack back to BGRA.
        using var encoder = new VideoToolboxVideoEncoder(width * 2, height, fps: 30, bitrate: 12_000_000);
        using var decoder = new VideoToolboxVideoDecoder();

        bool decoded = false;
        nint decodedSurface = 0;
        for (int i = 0; i < 30 && !decoded; i++)
        {
            byte[]? accessUnit = encoder.EncodeIOSurface(packed.Handle.Handle);
            if (accessUnit is null)
            {
                continue;
            }

            if (decoder.Decode(accessUnit, 0, out _, out _, out _))
            {
                decoded = true;
                decodedSurface = decoder.OutputIOSurface;
            }
        }

        if (!decoded || decodedSurface == 0)
        {
            Console.WriteLine("ALPHA-ROUNDTRIP-FAIL: VideoToolbox produced no decoded surface.");
            return false;
        }

        IOSurface.IOSurface unpacked = codec.Unpack(Wrap(decodedSurface));
        if ((int)unpacked.Width != width || (int)unpacked.Height != height)
        {
            Console.WriteLine($"ALPHA-ROUNDTRIP-FAIL: unpacked {unpacked.Width}x{unpacked.Height}, expected {width}x{height}.");
            return false;
        }

        // The codec is lossy, so check each band's alpha AND colour are preserved within a tolerance
        // (colour rides the left half through the same BT.709 round-trip as the opaque path).
        int maxAlphaError = 0;
        int maxColourError = 0;
        WithLockedBytes(unpacked, readOnly: true, (bytes, stride) =>
        {
            for (int b = 0; b < bands.Length; b++)
            {
                int y = (b * height / bands.Length) + (height / bands.Length / 2);
                int p = (y * stride) + ((width / 2) * 4);
                maxAlphaError = Math.Max(maxAlphaError, Math.Abs(bytes[p + 3] - bands[b].A));
                maxColourError = Math.Max(maxColourError, Math.Abs(bytes[p] - bands[b].B));
                maxColourError = Math.Max(maxColourError, Math.Abs(bytes[p + 1] - bands[b].G));
                maxColourError = Math.Max(maxColourError, Math.Abs(bytes[p + 2] - bands[b].R));
            }
        });

        const int alphaTolerance = 20;
        const int colourTolerance = 24;
        bool alphaOk = maxAlphaError <= alphaTolerance && maxColourError <= colourTolerance;
        Console.WriteLine(alphaOk
            ? $"ALPHA-ROUNDTRIP-OK: alpha (err {maxAlphaError}<={alphaTolerance}) + colour (err {maxColourError}<={colourTolerance}) preserved through encode/decode."
            : $"ALPHA-ROUNDTRIP-FAIL: alpha err {maxAlphaError} (<= {alphaTolerance}), colour err {maxColourError} (<= {colourTolerance}).");
        return alphaOk;
    }

    /// <summary>Read back the packed surface and assert left half = colour, right half = grey(alpha).</summary>
    private static bool VerifyPackedLayout(IOSurface.IOSurface packed, int width, int height, (byte B, byte G, byte R, byte A)[] bands)
    {
        bool ok = true;
        WithLockedBytes(packed, readOnly: true, (bytes, stride) =>
        {
            for (int b = 0; b < bands.Length; b++)
            {
                int y = (b * height / bands.Length) + (height / bands.Length / 2);
                int colour = (y * stride) + ((width / 2) * 4);          // mid of the left (colour) half
                int alpha = (y * stride) + ((width + (width / 2)) * 4);  // mid of the right (alpha) half

                if (bytes[colour] != bands[b].B || bytes[colour + 1] != bands[b].G || bytes[colour + 2] != bands[b].R)
                {
                    Console.WriteLine($"ALPHA-PACK-FAIL: band {b} colour mismatch.");
                    ok = false;
                    return;
                }

                byte a = bands[b].A;
                if (bytes[alpha] != a || bytes[alpha + 1] != a || bytes[alpha + 2] != a)
                {
                    Console.WriteLine($"ALPHA-PACK-FAIL: band {b} alpha-as-grey mismatch (expected {a}, got {bytes[alpha]}).");
                    ok = false;
                    return;
                }
            }
        });

        return ok;
    }

    /// <summary>Fill a BGRA IOSurface with horizontal colour+alpha bands (respecting row stride).</summary>
    private static void FillBandsBgra(IOSurface.IOSurface surface, int width, int height, (byte B, byte G, byte R, byte A)[] bands) =>
        WithLockedBytes(surface, readOnly: false, (bytes, stride) =>
        {
            for (int y = 0; y < height; y++)
            {
                (byte B, byte G, byte R, byte A) c = bands[Math.Min(y * bands.Length / height, bands.Length - 1)];
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int p = row + (x * 4);
                    bytes[p] = c.B;
                    bytes[p + 1] = c.G;
                    bytes[p + 2] = c.R;
                    bytes[p + 3] = c.A;
                }
            }
        });

    /// <summary>Fill a BGRA IOSurface with neutral grey (respecting the surface's row stride).</summary>
    private static void FillGreyBgra(IOSurface.IOSurface surface)
    {
        int width = (int)surface.Width;
        int height = (int)surface.Height;
        WithLockedBytes(surface, readOnly: false, (bytes, stride) =>
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int p = row + (x * 4);
                    bytes[p] = 130;     // B
                    bytes[p + 1] = 130; // G
                    bytes[p + 2] = 130; // R
                    bytes[p + 3] = 255; // A
                }
            }
        });
    }
}
#endif
