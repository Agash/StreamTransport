namespace Agash.StreamTransport.Codecs.FFmpeg;

/// <summary>
/// A family of FFmpeg video encoders: one hardware API or library. Each family is a separate
/// <see cref="FFmpegVideoEncoderFactory"/>, so a pipeline can rank them, fall back from one to the
/// next, and pin one when the application prefers it.
/// </summary>
public enum EncoderBackend
{
    /// <summary>Direct3D 12 Video Encode (Windows): any vendor's encoder, fed Direct3D 12 resources.</summary>
    D3D12,

    /// <summary>NVIDIA NVENC, fed Direct3D 11 textures, CUDA or system memory.</summary>
    Nvenc,

    /// <summary>AMD AMF, fed Direct3D 11 textures or system memory.</summary>
    Amf,

    /// <summary>Intel Quick Sync Video through oneVPL, fed Direct3D 11 or VA-API surfaces.</summary>
    Qsv,

    /// <summary>Vulkan Video, fed Vulkan images, DMA-BUFs mapped into them, or system memory.</summary>
    Vulkan,

    /// <summary>VA-API (Linux), fed DMA-BUFs or system memory.</summary>
    Vaapi,

    /// <summary>Apple VideoToolbox, fed IOSurfaces or system memory.</summary>
    VideoToolbox,

    /// <summary>Media Foundation transforms (Windows), fed system memory.</summary>
    MediaFoundation,

    /// <summary>Software encoders (kvazaar, OpenH264, SVT-AV1), fed system memory.</summary>
    Software,
}
