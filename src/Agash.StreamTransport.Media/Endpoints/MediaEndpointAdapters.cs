namespace Agash.StreamTransport.Media;

/// <summary>
/// An open video input over any <see cref="IVideoSource"/>: what a provider returns when its source type
/// does not implement <see cref="IVideoInput"/> itself.
/// </summary>
/// <param name="source">The source.</param>
/// <param name="info">What was opened.</param>
/// <param name="mode">The mode it captures in; null when it delivers whatever its producer sends.</param>
/// <param name="owner">What disposing the input releases: usually the source itself.</param>
public sealed class VideoInput(
    IVideoSource source,
    VideoInputInfo info,
    VideoInputMode? mode = null,
    IDisposable? owner = null
) : IVideoInput
{
    private int _disposed;

    /// <inheritdoc/>
    public VideoInputInfo Info { get; } = info ?? throw new ArgumentNullException(nameof(info));

    /// <inheritdoc/>
    public VideoInputMode? Mode { get; } = mode;

    /// <inheritdoc/>
    public IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints) =>
        source.Connect(consumer, constraints);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            (owner ?? source as IDisposable)?.Dispose();
        }
    }
}

/// <summary>An open audio input over any <see cref="IAudioSource"/>.</summary>
/// <param name="source">The source.</param>
/// <param name="info">What was opened.</param>
/// <param name="owner">What disposing the input releases: usually the source itself.</param>
public sealed class AudioInput(IAudioSource source, AudioInputInfo info, IDisposable? owner = null)
    : IAudioInput
{
    private int _disposed;

    /// <inheritdoc/>
    public AudioInputInfo Info { get; } = info ?? throw new ArgumentNullException(nameof(info));

    /// <inheritdoc/>
    public AudioFormat Format => source.Format;

    /// <inheritdoc/>
    public IDisposable Connect(IAudioFrameConsumer consumer) => source.Connect(consumer);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            (owner ?? source as IDisposable)?.Dispose();
        }
    }
}

/// <summary>A video output over any <see cref="IVideoSink"/>.</summary>
/// <param name="sink">The sink.</param>
/// <param name="provider">The provider that made it.</param>
/// <param name="name">The name it is published under.</param>
/// <param name="owner">What disposing the output releases: usually the sink itself.</param>
public sealed class VideoOutput(
    IVideoSink sink,
    string provider,
    string name,
    IDisposable? owner = null
) : IVideoOutput
{
    private int _disposed;

    /// <inheritdoc/>
    public string Provider { get; } = provider;

    /// <inheritdoc/>
    public string Name { get; } = name;

    /// <inheritdoc/>
    public VideoConstraints Constraints => sink.Constraints;

    /// <inheritdoc/>
    public void OnFrame(in VideoFrame frame) => sink.OnFrame(in frame);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            (owner ?? sink as IDisposable)?.Dispose();
        }
    }
}

/// <summary>An audio output over any <see cref="IAudioSink"/>.</summary>
/// <param name="sink">The sink.</param>
/// <param name="provider">The provider that made it.</param>
/// <param name="name">The device or name it plays to.</param>
/// <param name="owner">What disposing the output releases: usually the sink itself.</param>
public sealed class AudioOutput(
    IAudioSink sink,
    string provider,
    string name,
    IDisposable? owner = null
) : IAudioOutput
{
    private int _disposed;

    /// <inheritdoc/>
    public string Provider { get; } = provider;

    /// <inheritdoc/>
    public string Name { get; } = name;

    /// <inheritdoc/>
    public AudioConstraints Constraints => sink.Constraints;

    /// <inheritdoc/>
    public void OnFrame(in AudioFrame frame) => sink.OnFrame(in frame);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            (owner ?? sink as IDisposable)?.Dispose();
        }
    }
}
