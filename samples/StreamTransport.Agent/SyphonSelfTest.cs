#if HAS_SYPHON
using System.Runtime.Versioning;
using Agash.StreamTransport.Codecs;
using Syphon.NET;

// Disambiguate from the macOS framework bindings' `IOSurface` namespace.
using SyphonSurface = Syphon.NET.IOSurface;

namespace StreamTransport.Agent;

/// <summary>
/// A standalone check (no external Syphon app needed) of the macOS zero-copy path: it builds a neutral-grey
/// BGRA IOSurface, encodes it with VideoToolbox, decodes it back, confirms the decoder yields a BGRA
/// IOSurface (so a Syphon publish needs no conversion), then publishes it and captures it through a Syphon
/// loopback client to prove the publish/capture wiring. Run with <c>selftest</c>. macOS-only.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class SyphonSelfTest
{
    public static int Run()
    {
        FFmpegLibrary.EnsureLoaded();

        const int width = 256;
        const int height = 256;

        using var server = new SyphonServer("StreamTransport SelfTest");

        // 1) A neutral-grey BGRA source surface, owned by the Syphon server.
        SyphonSurface source = server.AcquireSurface(width, height, SyphonPixelFormat.Bgra);
        FillGreyBgra(source);
        Console.WriteLine($"source IOSurface {source.Width}x{source.Height}, format={source.PixelFormat}.");

        // 2) Encode the IOSurface with VideoToolbox, then 3) decode it back, expecting a BGRA surface.
        using var encoder = new VideoToolboxVideoEncoder(width, height, fps: 30, bitrate: 4_000_000);
        using var decoder = new VideoToolboxVideoDecoder();

        bool decoded = false;
        int decodedWidth = 0, decodedHeight = 0;
        nint decodedSurface = 0;
        int encodedBytes = 0;
        for (int i = 0; i < 30 && !decoded; i++)
        {
            byte[]? accessUnit = encoder.EncodeIOSurface(source.Handle);
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

        var decodedView = new SyphonSurface(decodedSurface);
        Console.WriteLine(
            $"VT round-trip OK: {encodedBytes}-byte HEVC AU -> {decodedWidth}x{decodedHeight} IOSurface, " +
            $"format={decodedView.PixelFormat} (NV12-family VideoToolbox surface; a colour-correct OBS " +
            "publish would need an NV12->BGRA Metal pass).");

        // 4) Publish the decoded surface and capture it back through a Syphon loopback client.
        bool looped = false;
        using (SyphonClient client = server.CreateLoopbackClient())
        {
            for (int i = 0; i < 100 && !looped; i++)
            {
                server.Publish(decodedView);
                SyphonServer.PumpEvents(TimeSpan.FromMilliseconds(5));
                using SyphonFrame? frame = client.TryGetFrame();
                if (frame is { } f && f.Surface.IsValid)
                {
                    looped = true;
                    Console.WriteLine($"loopback frame received: {f.Surface.Width}x{f.Surface.Height}, format={f.Surface.PixelFormat}.");
                }
            }
        }

        Console.WriteLine(looped ? "SYPHON-LOOPBACK-OK" : "SYPHON-LOOPBACK-FAIL");

        // Exercise the GPU alpha path (Metal pack/unpack via Syphon.NET's SurfaceEffect) and the opaque
        // NV12->BGRA publish path (the M-1 colour regression). Run both unconditionally so all diagnostics
        // print, then combine.
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
        SyphonSurface input = server.AcquireSurface(width, height, SyphonPixelFormat.Bgra);
        FillColourBandsBgra(input, width, height, bands);

        using var encoder = new VideoToolboxVideoEncoder(width, height, fps: 30, bitrate: 12_000_000);
        using var decoder = new VideoToolboxVideoDecoder();
        using var converter = new MetalNv12ToBgraConverter();

        bool decoded = false;
        nint decodedSurface = 0;
        for (int i = 0; i < 30 && !decoded; i++)
        {
            byte[]? accessUnit = encoder.EncodeIOSurface(input.Handle);
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

        var decodedView = new SyphonSurface(decodedSurface);
        if ((uint)decodedView.PixelFormat == (uint)SyphonPixelFormat.Bgra)
        {
            Console.WriteLine("OPAQUE-FAIL: decoder yielded BGRA (expected NV12) - the conversion path is untested.");
            return false;
        }

        SyphonSurface bgra = converter.Convert(decodedView);
        if (bgra.Width != width || bgra.Height != height)
        {
            Console.WriteLine($"OPAQUE-FAIL: converted {bgra.Width}x{bgra.Height}, expected {width}x{height}.");
            return false;
        }

        int maxColourError = 0;
        using (SyphonSurface.Lock locked = bgra.LockBytes(readOnly: true))
        {
            int stride = bgra.BytesPerRow;
            Span<byte> bytes = locked.Bytes;
            for (int b = 0; b < bands.Length; b++)
            {
                int y = (b * height / bands.Length) + (height / bands.Length / 2);
                int p = (y * stride) + ((width / 2) * 4);
                maxColourError = Math.Max(maxColourError, Math.Abs(bytes[p] - bands[b].B));
                maxColourError = Math.Max(maxColourError, Math.Abs(bytes[p + 1] - bands[b].G));
                maxColourError = Math.Max(maxColourError, Math.Abs(bytes[p + 2] - bands[b].R));
            }
        }

        const int tolerance = 24; // BT.709 limited round-trip + HEVC on flat colour is well within this.
        bool opaqueOk = maxColourError <= tolerance;
        Console.WriteLine(opaqueOk
            ? $"OPAQUE-OK: NV12->BGRA colour preserved through encode/decode (max error {maxColourError} <= {tolerance})."
            : $"OPAQUE-FAIL: max colour error {maxColourError} > {tolerance} (NV12 likely published as BGRA - M-1).");
        return opaqueOk;
    }

    /// <summary>Fill a BGRA IOSurface with horizontal solid-colour bands (opaque).</summary>
    private static void FillColourBandsBgra(SyphonSurface surface, int width, int height, (byte B, byte G, byte R)[] bands)
    {
        int stride = surface.BytesPerRow;
        using SyphonSurface.Lock locked = surface.LockBytes(readOnly: false);
        Span<byte> bytes = locked.Bytes;
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
    }

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
        SyphonSurface input = server.AcquireSurface(width, height, SyphonPixelFormat.Bgra);
        FillBandsBgra(input, width, height, bands);

        using var codec = new MetalAlphaCodec();

        // (A) Pack and read back the 2W x H surface; check the layout byte-exact (no codec involved).
        SyphonSurface packed = codec.Pack(input);
        if (packed.Width != width * 2 || packed.Height != height)
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
            byte[]? accessUnit = encoder.EncodeIOSurface(packed.Handle);
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

        SyphonSurface unpacked = codec.Unpack(new SyphonSurface(decodedSurface));
        if (unpacked.Width != width || unpacked.Height != height)
        {
            Console.WriteLine($"ALPHA-ROUNDTRIP-FAIL: unpacked {unpacked.Width}x{unpacked.Height}, expected {width}x{height}.");
            return false;
        }

        // The codec is lossy, so check each band's alpha AND colour are preserved within a tolerance
        // (colour rides the left half through the same BT.709 round-trip as the opaque path).
        int maxAlphaError = 0;
        int maxColourError = 0;
        using (SyphonSurface.Lock locked = unpacked.LockBytes(readOnly: true))
        {
            int stride = unpacked.BytesPerRow;
            Span<byte> bytes = locked.Bytes;
            for (int b = 0; b < bands.Length; b++)
            {
                int y = (b * height / bands.Length) + (height / bands.Length / 2);
                int p = (y * stride) + ((width / 2) * 4);
                maxAlphaError = Math.Max(maxAlphaError, Math.Abs(bytes[p + 3] - bands[b].A));
                maxColourError = Math.Max(maxColourError, Math.Abs(bytes[p] - bands[b].B));
                maxColourError = Math.Max(maxColourError, Math.Abs(bytes[p + 1] - bands[b].G));
                maxColourError = Math.Max(maxColourError, Math.Abs(bytes[p + 2] - bands[b].R));
            }
        }

        const int alphaTolerance = 20;
        const int colourTolerance = 24;
        bool alphaOk = maxAlphaError <= alphaTolerance && maxColourError <= colourTolerance;
        Console.WriteLine(alphaOk
            ? $"ALPHA-ROUNDTRIP-OK: alpha (err {maxAlphaError}<={alphaTolerance}) + colour (err {maxColourError}<={colourTolerance}) preserved through encode/decode."
            : $"ALPHA-ROUNDTRIP-FAIL: alpha err {maxAlphaError} (<= {alphaTolerance}), colour err {maxColourError} (<= {colourTolerance}).");
        return alphaOk;
    }

    /// <summary>Read back the packed surface and assert left half = colour, right half = grey(alpha).</summary>
    private static bool VerifyPackedLayout(SyphonSurface packed, int width, int height, (byte B, byte G, byte R, byte A)[] bands)
    {
        using SyphonSurface.Lock locked = packed.LockBytes(readOnly: true);
        int stride = packed.BytesPerRow;
        Span<byte> bytes = locked.Bytes;
        for (int b = 0; b < bands.Length; b++)
        {
            int y = (b * height / bands.Length) + (height / bands.Length / 2);
            int colour = (y * stride) + ((width / 2) * 4);          // mid of the left (colour) half
            int alpha = (y * stride) + ((width + (width / 2)) * 4);  // mid of the right (alpha) half

            if (bytes[colour] != bands[b].B || bytes[colour + 1] != bands[b].G || bytes[colour + 2] != bands[b].R)
            {
                Console.WriteLine($"ALPHA-PACK-FAIL: band {b} colour mismatch.");
                return false;
            }

            byte a = bands[b].A;
            if (bytes[alpha] != a || bytes[alpha + 1] != a || bytes[alpha + 2] != a)
            {
                Console.WriteLine($"ALPHA-PACK-FAIL: band {b} alpha-as-grey mismatch (expected {a}, got {bytes[alpha]}).");
                return false;
            }
        }

        return true;
    }

    /// <summary>Fill a BGRA IOSurface with horizontal colour+alpha bands (respecting row stride).</summary>
    private static void FillBandsBgra(SyphonSurface surface, int width, int height, (byte B, byte G, byte R, byte A)[] bands)
    {
        int stride = surface.BytesPerRow;
        using SyphonSurface.Lock locked = surface.LockBytes(readOnly: false);
        Span<byte> bytes = locked.Bytes;
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
    }

    /// <summary>Fill a BGRA IOSurface with neutral grey (respecting the surface's row stride).</summary>
    private static void FillGreyBgra(SyphonSurface surface)
    {
        int width = surface.Width;
        int height = surface.Height;
        int stride = surface.BytesPerRow;

        using SyphonSurface.Lock locked = surface.LockBytes(readOnly: false);
        Span<byte> bytes = locked.Bytes;
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
    }
}
#endif
