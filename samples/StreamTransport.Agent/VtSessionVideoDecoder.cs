#if HAS_SYPHON
using System.Buffers.Binary;
using System.Runtime.Versioning;
using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using CoreMedia;
using CoreVideo;
using Foundation;
using IOSurface;
using ObjCRuntime;
using VideoToolbox;

namespace StreamTransport.Agent;

/// <summary>
/// macOS hardware HEVC decoder built directly on <see cref="VTDecompressionSession"/> (Microsoft VideoToolbox
/// bindings), bypassing FFmpeg's VideoToolbox hwaccel. It decodes straight into <b>BGRA</b> IOSurface-backed
/// CVPixelBuffers (Syphon's native format), so the decoded surface is announced to Syphon zero-copy with no
/// Metal NV12-&gt;BGRA pass. macOS-only.
/// </summary>
/// <remarks>
/// Why not FFmpeg's VT hwaccel: it drives an out-of-process (XPC) decode synchronously one frame at a time and
/// cannot select a BGRA output format, which collapsed the publish frame rate. A raw session gives us the
/// output pixel format, the CVPixelBuffer pool, and direct CVPixelBuffer-&gt;IOSurface access.
///
/// Input is Annex-B HEVC (start-code-delimited NALs, parameter sets in-band on IDR). VideoToolbox wants the
/// parameter sets as a CMVideoFormatDescription and the VCL NALs as length-prefixed (hvcC-style) sample data,
/// so each access unit is split, the VPS/SPS/PPS build/refresh the format description and session, and the VCL
/// NALs are rewritten with 4-byte big-endian length prefixes into a CMSampleBuffer.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed partial class VtSessionVideoDecoder : IVideoDecoderBackend
{
    private const int NalLengthSize = 4;

    public StreamInteropKind OutputSurfaceKind => StreamInteropKind.Syphon;
    public nint NativeDevice => 0;

    private VTDecompressionSession? _session;
    private CMVideoFormatDescription? _format;
    private byte[]? _vps;
    private byte[]? _sps;
    private byte[]? _pps;

    // The decoded CVPixelBuffer (and the one before it) are kept alive until superseded so the published
    // IOSurface stays valid while the encoder/Syphon consumes it (double-buffered, like the capture source).
    // We hold raw CFRetain'd CVPixelBuffer handles - the callback's CVImageBuffer is owned by VideoToolbox.
    private nint _currentPb;
    private nint _previousPb;
    private nint _outSurface;
    private int _outWidth;
    private int _outHeight;
    private bool _disposed;

    public bool TryDecode(ReadOnlySpan<byte> accessUnit, uint rtpTimestamp, long presentationTimeNs, out VideoFrame frame, out uint frameRtpTimestamp)
    {
        frame = default;
        frameRtpTimestamp = rtpTimestamp;
        if (_disposed)
        {
            return false;
        }

        // Split the Annex-B access unit, refreshing parameter sets and collecting the VCL NALs as a
        // length-prefixed payload for the sample buffer.
        bool paramsChanged = false;
        var vcl = new System.IO.MemoryStream(accessUnit.Length + 16);
        Span<byte> len = stackalloc byte[NalLengthSize];
        foreach (Range nal in EnumerateAnnexBNals(accessUnit))
        {
            ReadOnlySpan<byte> unit = accessUnit[nal];
            if (unit.IsEmpty)
            {
                continue;
            }

            int nalType = (unit[0] >> 1) & 0x3F;
            switch (nalType)
            {
                case 32: paramsChanged |= ReplaceIfChanged(ref _vps, unit); break; // VPS
                case 33: paramsChanged |= ReplaceIfChanged(ref _sps, unit); break; // SPS
                case 34: paramsChanged |= ReplaceIfChanged(ref _pps, unit); break; // PPS
                case 35: case 36: break;                                           // AUD / EOS - skip
                default:
                    if (nalType <= 31) // VCL NAL: emit length-prefixed
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)unit.Length);
                        vcl.Write(len);
                        vcl.Write(unit);
                    }
                    break;
            }
        }

        if ((paramsChanged || _session is null) && _vps is not null && _sps is not null && _pps is not null)
        {
            RebuildSession();
        }

        if (_session is null || vcl.Length == 0)
        {
            return false; // still waiting for parameter sets, or a parameter-set-only access unit
        }

        if (!vcl.TryGetBuffer(out ArraySegment<byte> vclBuffer))
        {
            vclBuffer = vcl.ToArray();
        }

        if (!Decode(vclBuffer, rtpTimestamp))
        {
            return false;
        }

        frame = VideoFrame.FromSurface(_outSurface, StreamInteropKind.Syphon, _outWidth, _outHeight, presentationTimeNs)
            with { PixelFormat = VideoPixelFormat.Bgra };
        return true;
    }

    private bool Decode(ArraySegment<byte> lengthPrefixedVcl, uint rtpTimestamp)
    {
        var block = CMBlockBuffer.FromMemoryBlock(
            lengthPrefixedVcl.Array!.AsSpan(lengthPrefixedVcl.Offset, lengthPrefixedVcl.Count).ToArray(),
            (nuint)0, CMBlockBufferFlags.AssureMemoryNow, out CMBlockBufferError blockError);
        if (block is null || blockError != CMBlockBufferError.None)
        {
            return false;
        }

        using (block)
        {
            var timing = new CMSampleTimingInfo
            {
                Duration = CMTime.Invalid,
                PresentationTimeStamp = new CMTime(rtpTimestamp, 90_000),
                DecodeTimeStamp = CMTime.Invalid,
            };

            var sample = CMSampleBuffer.CreateReady(
                block, _format!, 1, [timing], [(nuint)lengthPrefixedVcl.Count], out CMSampleBufferError sampleError);
            if (sample is null || sampleError != CMSampleBufferError.None)
            {
                sample?.Dispose();
                return false;
            }

            using (sample)
            {
                // Synchronous decode (no async flag): the output callback fires within DecodeFrame on this
                // thread, so _outSurface is set before we return.
                _outSurface = 0;
                VTStatus status = _session!.DecodeFrame(sample, VTDecodeFrameFlags.EnableTemporalProcessing,
                    sourceFrame: IntPtr.Zero, out VTDecodeInfoFlags _);
                return status == VTStatus.Ok && _outSurface != 0;
            }
        }
    }

    private void OnDecodedFrame(IntPtr sourceFrame, VTStatus status, VTDecodeInfoFlags flags,
        CVImageBuffer? imageBuffer, CMTime presentationTimeStamp, CMTime presentationDuration)
    {
        if (status != VTStatus.Ok || imageBuffer is null)
        {
            return;
        }

        nint handle = imageBuffer.Handle.Handle;
        if (handle == 0)
        {
            return;
        }

        // VideoToolbox owns the callback's CVPixelBuffer; wrap it non-owning to read it.
        var pixelBuffer = Runtime.GetINativeObject<CVPixelBuffer>(handle, owns: false);
        IOSurface.IOSurface? surface = pixelBuffer?.GetIOSurface();
        if (surface is null)
        {
            return;
        }

        // Keep this buffer (and the prior one) alive so the announced IOSurface stays valid while Syphon/the
        // encoder reads it: hold an explicit CFRetain across the callback boundary, release the superseded one.
        if (_previousPb != 0) { CFRelease(_previousPb); }
        _previousPb = _currentPb;
        _currentPb = handle;
        CFRetain(_currentPb);

        _outSurface = surface.Handle.Handle;
        _outWidth = (int)pixelBuffer!.Width;
        _outHeight = (int)pixelBuffer.Height;
    }

    [System.Runtime.InteropServices.LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial nint CFRetain(nint cf);

    [System.Runtime.InteropServices.LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(nint cf);

    private void RebuildSession()
    {
        _session?.Dispose();
        _session = null;
        _format?.Dispose();

        _format = CMVideoFormatDescription.FromHevcParameterSets(
            [_vps!, _sps!, _pps!], NalLengthSize, new NSDictionary(), out CMFormatDescriptionError formatError);
        if (_format is null || formatError != CMFormatDescriptionError.None)
        {
            _format = null;
            return;
        }

        var spec = new VTVideoDecoderSpecification { EnableHardwareAcceleratedVideoDecoder = true };
        var destination = new CVPixelBufferAttributes
        {
            PixelFormatType = CVPixelFormatType.CV32BGRA, // decode straight to Syphon's BGRA - no Metal convert
            AllocateWithIOSurface = true,                 // IOSurface-backed for the zero-copy Syphon announce
            MetalCompatibility = true,
        };

        _session = VTDecompressionSession.Create(OnDecodedFrame, _format, spec, destination);
    }

    private static bool ReplaceIfChanged(ref byte[]? current, ReadOnlySpan<byte> next)
    {
        if (current is not null && next.SequenceEqual(current))
        {
            return false;
        }

        current = next.ToArray();
        return true;
    }

    /// <summary>Enumerate the NAL units in an Annex-B buffer (3- or 4-byte start codes), yielding payload ranges.</summary>
    private static IEnumerable<Range> EnumerateAnnexBNals(ReadOnlySpan<byte> data)
    {
        // Span can't be captured by an iterator, so collect ranges eagerly.
        var ranges = new List<Range>();
        int i = 0, n = data.Length;
        int start = -1;
        while (i + 2 < n)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start >= 0)
                {
                    int end = i;
                    if (end > 0 && data[end - 1] == 0) end--; // trim the 4th zero of a 4-byte start code
                    ranges.Add(new Range(start, end));
                }
                i += 3;
                start = i;
            }
            else
            {
                i++;
            }
        }
        if (start >= 0 && start < n)
        {
            ranges.Add(new Range(start, n));
        }
        return ranges;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session?.Dispose();
        _format?.Dispose();
        if (_currentPb != 0) { CFRelease(_currentPb); _currentPb = 0; }
        if (_previousPb != 0) { CFRelease(_previousPb); _previousPb = 0; }
    }
}
#endif
