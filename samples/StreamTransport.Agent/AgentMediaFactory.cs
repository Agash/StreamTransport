using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace StreamTransport.Agent;

/// <summary>
/// DI-registered factory for the platform-native capture sources and publish sinks. It is the single place
/// these objects are constructed, so cross-cutting dependencies (the host <see cref="ILoggerFactory"/>, for
/// PipeWire stream tracing under <c>--verbose</c>) are injected once here rather than threaded through the
/// run loop. Runtime arguments (node/sender/server name, alpha, fps) are passed per call. Each platform's
/// methods compile only under that platform's build symbol and carry its <c>[SupportedOSPlatform]</c>, so
/// callers keep the OS guard the underlying APIs require.
/// </summary>
internal sealed class AgentMediaFactory(ILoggerFactory loggerFactory)
{
    /// <summary>The host logger factory, injected so platform sinks can route diagnostics through --verbose.</summary>
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;

#if WINDOWS_HEAD
    public SpoutVideoCaptureSource CreateSpoutCapture(string? senderName, string encoderName, bool alpha) =>
        new(senderName, encoderName, alpha);

    public SpoutVideoPublishSink CreateSpoutPublish(string senderName, bool alpha) =>
        new(senderName, alpha);

    public WasapiAudioCaptureSource CreateWasapiCapture(bool loopback) => new(loopback);

    public WasapiAudioPublishSink CreateWasapiPublish() => new();
#endif

#if HAS_PIPEWIRE
    [SupportedOSPlatform("linux")]
    public Task<PipeWireVideoCaptureSource> CreatePipeWireCaptureAsync(uint targetNodeId, bool alpha) =>
        PipeWireVideoCaptureSource.CreateAsync(targetNodeId, alpha, LoggerFactory);

    [SupportedOSPlatform("linux")]
    public Task<PipeWireVideoPublishSink> CreatePipeWirePublishAsync(string nodeName, bool alpha, int frameRate = 30) =>
        PipeWireVideoPublishSink.CreateAsync(nodeName, alpha, frameRate, LoggerFactory);

    [SupportedOSPlatform("linux")]
    public Task<PipeWireAudioPublishSink> CreatePipeWireAudioPublishAsync(string nodeName) =>
        PipeWireAudioPublishSink.CreateAsync(nodeName, LoggerFactory);
#endif

#if HAS_SYPHON
    [SupportedOSPlatform("macos")]
    public SyphonVideoCaptureSource CreateSyphonCapture(string? serverName, bool alpha) =>
        SyphonVideoCaptureSource.Connect(serverName, alpha);

    [SupportedOSPlatform("macos")]
    public SyphonVideoPublishSink CreateSyphonPublish(string serverName, bool alpha) =>
        new(serverName, alpha);
#endif

#if MACOS_HEAD
    public CoreAudioPublishSink CreateCoreAudioPublish() => new();
#endif
}
