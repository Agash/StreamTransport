using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Windows.Direct3D12;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace Agash.StreamTransport.Windows.MediaFoundation;

/// <summary>
/// An open Media Foundation camera. A thread reads samples while a consumer is connected and hands each
/// to every consumer. A sample's time is the camera's capture time on the system's performance counter,
/// which is the media clock on Windows.
/// </summary>
/// <remarks>
/// When every consumer takes NV12 in Direct3D 11 textures on one adapter (a hardware encoder does), the
/// camera is read through Media Foundation's Direct3D 11 transforms on that adapter's shared device: its
/// video processor uploads the picture and converts it to NV12 on the GPU, and the encoder reads the
/// texture where it is, on the same device. Otherwise frames are handed over in the sample's own memory,
/// as the camera sends them. The reader is made again when the consumers change which they take.
/// </remarks>
internal sealed unsafe partial class MediaFoundationVideoInput : IVideoInput, IVideoFrameRetainer
{
    // How long a stop waits for the reader to cancel the sample it asked for.
    private static readonly TimeSpan FlushWait = TimeSpan.FromSeconds(5);

    // How far a sample time may stand from now and still be taken for a capture time on the media clock.
    private static readonly TimeSpan PlausibleSkew = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    // Serialises starting and stopping the reading thread; never taken under _gate.
    private readonly Lock _running = new();
    private readonly VideoInputMode _mode;
    private readonly uint _nativeType;
    private readonly VideoColor _nativeColor;
    private ImmutableArray<(
        IVideoFrameConsumer Consumer,
        VideoConstraints Constraints
    )> _consumers = [];

    // The camera's source and its reader, and what the reader delivers. Each reader has a source of its
    // own: a source a reader has streamed from does not always start again for the next one. Both are
    // replaced only while no request is outstanding.
    private IMFMediaSource* _source;
    private IMFSourceReader* _reader;
    private IMFDXGIDeviceManager* _manager;
    private GpuIdentity? _readerGpu;
    private VideoFormat _output;
    private VideoColor _color;

    // The reader's callback, and the lock its answers run under: one sample is asked for at a time, so
    // they never overlap, and stopping takes the lock to know no answer is running.
    private readonly ReaderCallback* _callback;
    private readonly Lock _answers = new();
    private readonly ManualResetEventSlim _flushed = new();
    private bool _reading;
    private bool _stopping;
    private bool _firstFrameLogged;
    private bool _disposed;

    // The sample whose frame the consumers are being handed, for Retain.
    private IMFSample* _delivering;

    public MediaFoundationVideoInput(
        VideoInputInfo info,
        VideoInputMode mode,
        ILoggerFactory loggers
    )
    {
        Info = info;
        _mode = mode;
        _logger = loggers.CreateLogger<MediaFoundationVideoInput>();
        MediaFoundationPlatform.Start();
        _source = CreateSource(info.Id);
        try
        {
            // A reader of its own checks the mode; reading opens another, asynchronous one.
            _reader = MediaFoundationPlatform.Reader(_source);
            uint? found = null;
            foreach (
                (VideoInputMode native, uint index) in MediaFoundationPlatform.NativeTypes(_reader)
            )
            {
                if (native == mode)
                {
                    found = index;
                    break;
                }
            }

            _nativeType =
                found ?? throw new NotSupportedException($"{info.Name} no longer offers {mode}.");
            _nativeColor = SetNativeType(_reader);
            _output = new VideoFormat(mode.PixelFormat, mode.Size.Width, mode.Size.Height);
            _color = _nativeColor;
            LogOpened(info.Name, mode);
        }
        finally
        {
            ReleaseReader();
        }

        _callback = ReaderCallback.Create(this);
    }

    public VideoInputInfo Info { get; }

    public VideoInputMode? Mode => _mode;

    public IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(constraints);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _consumers = _consumers.Add((consumer, constraints));
        }

        Reconcile();
        return new Connection(this, consumer);
    }

    // Keeps the sample being delivered, which holds the texture its frame is in; Media Foundation reuses
    // the texture once every hold on the sample is released.
    public VideoFrameLease Retain(in VideoFrame frame)
    {
        IMFSample* sample = _delivering;
        if (sample is null)
        {
            throw new InvalidOperationException(
                "A Media Foundation frame can only be kept while it is being delivered."
            );
        }

        _ = sample->AddRef();
        return SampleLease.Of(in frame, sample);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _consumers = [];
        }

        lock (_running)
        {
            StopReading();
        }

        _ = ((IUnknown*)_callback)->Release();
    }

    private static IMFMediaSource* CreateSource(string symbolicLink)
    {
        IMFAttributes* attributes;
        Win32.MFCreateAttributes(&attributes, 2).ThrowOnFailure();
        try
        {
            Guid sourceType = Win32.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
            Guid videoCapture = Win32.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID;
            Guid linkKey = Win32.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK;
            attributes->SetGUID(&sourceType, &videoCapture);
            fixed (char* link = symbolicLink)
            {
                attributes->SetString(&linkKey, link);
            }

            IMFMediaSource* source;
            Win32.MFCreateDeviceSource(attributes, &source).ThrowOnFailure();
            return source;
        }
        finally
        {
            _ = attributes->Release();
        }
    }

    // The adapter every consumer takes NV12 Direct3D 11 textures on, or null when one of them does not.
    private static GpuIdentity? SharedGpu(
        ImmutableArray<(IVideoFrameConsumer Consumer, VideoConstraints Constraints)> consumers
    )
    {
        GpuIdentity? shared = null;
        foreach ((_, VideoConstraints constraints) in consumers)
        {
            if (
                constraints.Device is not { Kind: GpuIdentityKind.DxgiAdapterLuid } device
                || !constraints.Storages.Contains(VideoStorageKind.D3D11)
                || !D3D11Bridge.IsAvailable(device)
                || !constraints.PixelFormats.Contains(PixelFormat.Nv12)
                || (shared is { } other && other != device)
            )
            {
                return null;
            }

            shared = device;
        }

        return shared;
    }

    // Starts, stops or remakes the reading for the consumers connected now.
    private void Reconcile()
    {
        lock (_running)
        {
            GpuIdentity? wanted;
            bool reading;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                wanted = SharedGpu(_consumers);
                reading = !_consumers.IsEmpty;
            }

            if (_reading && (!reading || wanted != _readerGpu))
            {
                StopReading();
            }

            if (!reading || _reading)
            {
                return;
            }

            OpenReader(wanted);
            _stopping = false;
            _firstFrameLogged = false;
            _reading = true;
            lock (_answers)
            {
                Request();
            }
        }
    }

    // A reader of the camera's mode, on a GPU's Direct3D 11 transforms or in memory. A reader that
    // cannot be made on the GPU (no video processor there) leaves the camera read in memory.
    private void OpenReader(GpuIdentity? gpu)
    {
        ReleaseReader();
        if (gpu is { } adapter)
        {
            try
            {
                OpenOnGpu(adapter);
                return;
            }
            catch (COMException exception)
            {
                LogGpuReaderFailed(exception, Info.Name, adapter);
                ReleaseReader();
            }
        }

        _source = CreateSource(Info.Id);
        _reader = MediaFoundationPlatform.Reader(_source, callback: (IUnknown*)_callback);
        _ = SetNativeType(_reader);
        _readerGpu = null;
        _output = new VideoFormat(_mode.PixelFormat, _mode.Size.Width, _mode.Size.Height);
        _color = _nativeColor;
    }

    private void OpenOnGpu(GpuIdentity adapter)
    {
        var bridge = D3D11Bridge.For(adapter);
        uint token;
        IMFDXGIDeviceManager* manager;
        Win32.MFCreateDXGIDeviceManager(&token, &manager).ThrowOnFailure();
        _manager = manager;
        manager->ResetDevice((IUnknown*)bridge.Device, token);
        _source = CreateSource(Info.Id);
        _reader = MediaFoundationPlatform.Reader(_source, manager, (IUnknown*)_callback);
        _ = SetNativeType(_reader);

        // NV12 out of the reader: its video processor converts on the GPU when the camera sends another
        // format, and the textures go to the encoder as they are.
        IMFMediaType* nv12;
        Win32.MFCreateMediaType(&nv12).ThrowOnFailure();
        try
        {
            Guid majorKey = Win32.MF_MT_MAJOR_TYPE;
            Guid video = Win32.MFMediaType_Video;
            Guid subtypeKey = Win32.MF_MT_SUBTYPE;
            Guid subtype = Win32.MFVideoFormat_NV12;
            nv12->SetGUID(&majorKey, &video);
            nv12->SetGUID(&subtypeKey, &subtype);
            _reader->SetCurrentMediaType(MediaFoundationPlatform.FirstVideoStream, null, nv12);
        }
        finally
        {
            _ = nv12->Release();
        }

        _readerGpu = adapter;
        ReadOutputType();
        LogReadingOnGpu(Info.Name, adapter);
    }

    // The camera's mode on a reader, and the colour it states.
    private VideoColor SetNativeType(IMFSourceReader* reader)
    {
        IMFMediaType* type;
        reader->GetNativeMediaType(MediaFoundationPlatform.FirstVideoStream, _nativeType, &type);
        try
        {
            reader->SetCurrentMediaType(MediaFoundationPlatform.FirstVideoStream, null, type);
            return MediaFoundationPlatform.Color(type, _mode.PixelFormat);
        }
        finally
        {
            _ = type->Release();
        }
    }

    // What the reader delivers now, after it was set up or changed its output.
    private void ReadOutputType()
    {
        IMFMediaType* type;
        _reader->GetCurrentMediaType(MediaFoundationPlatform.FirstVideoStream, &type);
        try
        {
            if (MediaFoundationPlatform.Describe(type) is { } described)
            {
                _output = new VideoFormat(
                    described.PixelFormat,
                    described.Size.Width,
                    described.Size.Height
                );
                _color = MediaFoundationPlatform.Color(type, described.PixelFormat);
            }
        }
        finally
        {
            _ = type->Release();
        }
    }

    private void ReleaseReader()
    {
        if (_reader is not null)
        {
            _ = _reader->Release();
            _reader = null;
        }

        if (_manager is not null)
        {
            _ = _manager->Release();
            _manager = null;
        }

        if (_source is not null)
        {
            _source->Shutdown();
            _ = _source->Release();
            _source = null;
        }

        _readerGpu = null;
    }

    // Stops reading: no further sample is asked for, the one asked for is cancelled with a flush (a camera
    // that sends nothing never answers it), and once the flush is done and no answer runs, the reader goes.
    private void StopReading()
    {
        if (!_reading)
        {
            return;
        }

        _reading = false;
        lock (_answers)
        {
            _stopping = true;
        }

        _flushed.Reset();
        try
        {
            _reader->Flush(MediaFoundationPlatform.FirstVideoStream);
            if (!_flushed.Wait(FlushWait))
            {
                LogFlushTimedOut(Info.Name);
            }
        }
        catch (COMException exception)
        {
            LogReadFailed(exception, Info.Name);
        }

        lock (_answers)
        {
            ReleaseReader();
        }
    }

    // Asks for the next sample; the callback answers. Called under _answers.
    private void Request()
    {
        try
        {
            _reader->ReadSample(
                MediaFoundationPlatform.FirstVideoStream,
                0,
                null,
                null,
                null,
                null
            );
        }
        catch (COMException exception)
        {
            LogReadFailed(exception, Info.Name);
        }
    }

    // A sample asked for, or the failure to get one, on a Media Foundation work queue thread.
    private void Answered(int status, uint flags, long time, IMFSample* sample)
    {
        lock (_answers)
        {
            if (_stopping)
            {
                return;
            }

            if (status < 0)
            {
                LogReadFailed(Marshal.GetExceptionForHR(status)!, Info.Name);
                return;
            }

            if (
                (flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0
            )
            {
                ReadOutputType();
            }

            if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
            {
                LogEnded(Info.Name);
                return;
            }

            // A stream tick or a format change carries no picture; the sample is the reader's.
            if (sample is not null)
            {
                Deliver(sample, time);
            }

            Request();
        }
    }

    private void Deliver(IMFSample* sample, long time)
    {
        MediaTimestamp timestamp = Timestamp(time);
        IMFMediaBuffer* buffer;
        sample->GetBufferByIndex(0, &buffer);
        try
        {
            Guid dxgiId = typeof(IMFDXGIBuffer).GUID;
            void* dxgi;
            if (_readerGpu is { } adapter && buffer->QueryInterface(&dxgiId, &dxgi).Succeeded)
            {
                try
                {
                    HandTexture((IMFDXGIBuffer*)dxgi, adapter, sample, timestamp);
                }
                finally
                {
                    _ = ((IUnknown*)dxgi)->Release();
                }

                return;
            }
        }
        finally
        {
            _ = buffer->Release();
        }

        HandMemory(sample, timestamp);
    }

    // A frame in a texture of the reader's device. The transforms finished it on that device's context,
    // which orders it before anything a consumer then does there.
    private void HandTexture(
        IMFDXGIBuffer* buffer,
        GpuIdentity adapter,
        IMFSample* sample,
        MediaTimestamp timestamp
    )
    {
        Guid textureId = typeof(ID3D11Texture2D).GUID;
        void* resource;
        buffer->GetResource(&textureId, &resource);
        try
        {
            uint subresource;
            buffer->GetSubresourceIndex(&subresource);
            D3D11_TEXTURE2D_DESC description;
            ((ID3D11Texture2D*)resource)->GetDesc(&description);

            // Media Foundation may align its textures past the picture.
            VideoFrame frame = new(
                new VideoStorage(new D3D11Image((nint)resource, (int)subresource, adapter)),
                new VideoFormat(
                    PixelFormat.Nv12,
                    new VideoSize((int)description.Width, (int)description.Height),
                    new VideoRect(0, 0, _output.CodedSize.Width, _output.CodedSize.Height)
                ),
                timestamp,
                color: _color,
                retainer: this
            );
            _delivering = sample;
            try
            {
                Hand(in frame);
            }
            finally
            {
                _delivering = null;
            }
        }
        finally
        {
            _ = ((IUnknown*)resource)->Release();
        }
    }

    private void HandMemory(IMFSample* sample, MediaTimestamp timestamp)
    {
        IMFMediaBuffer* buffer;
        sample->ConvertToContiguousBuffer(&buffer);
        try
        {
            Guid twoDimensional = typeof(IMF2DBuffer).GUID;
            void* planar;
            bool locked2D = buffer->QueryInterface(&twoDimensional, &planar).Succeeded;
            byte* scan0;
            int pitch;
            uint length;
            if (locked2D)
            {
                ((IMF2DBuffer*)planar)->Lock2D(&scan0, &pitch);
                ((IMF2DBuffer*)planar)->GetContiguousLength(&length);
            }
            else
            {
                uint max;
                buffer->Lock(&scan0, &max, &length);
                pitch = Stride(_output);
            }

            try
            {
                VideoFrame frame = Planes(
                    new ReadOnlySpan<byte>(scan0, (int)length),
                    pitch,
                    timestamp
                );
                Hand(in frame);
            }
            finally
            {
                if (locked2D)
                {
                    ((IMF2DBuffer*)planar)->Unlock2D();
                    _ = ((IUnknown*)planar)->Release();
                }
                else
                {
                    buffer->Unlock();
                }
            }
        }
        finally
        {
            _ = buffer->Release();
        }
    }

    private VideoFrame Planes(ReadOnlySpan<byte> all, int pitch, MediaTimestamp timestamp)
    {
        int height = _output.CodedSize.Height;
        return _output.PixelFormat switch
        {
            PixelFormat.Nv12 => VideoFrame.FromPlanes(
                _output,
                timestamp,
                all[..(pitch * height)],
                pitch,
                all[(pitch * height)..],
                pitch,
                color: _color
            ),
            PixelFormat.I420 => VideoFrame.FromPlanes(
                _output,
                timestamp,
                all[..(pitch * height)],
                pitch,
                all.Slice(pitch * height, pitch / 2 * (height / 2)),
                pitch / 2,
                all[((pitch * height) + (pitch / 2 * (height / 2)))..],
                pitch / 2,
                color: _color
            ),
            _ => VideoFrame.FromPlanes(_output, timestamp, all, pitch, color: _color),
        };
    }

    private void Hand(in VideoFrame frame)
    {
        if (!_firstFrameLogged)
        {
            _firstFrameLogged = true;
            LogFirstFrame(Info.Name, frame.Storage.Kind, frame.Format);
        }

        foreach ((IVideoFrameConsumer consumer, VideoConstraints constraints) in _consumers)
        {
            // A consumer that joined while the reader still delivers what it does not take waits for the
            // reader it asked for.
            if (!constraints.Accepts(frame.Storage, frame.Format.PixelFormat))
            {
                continue;
            }

            try
            {
                consumer.OnFrame(in frame);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One consumer's failure does not stop the others or the capture.
                LogConsumerFailed(exception);
            }
        }
    }

    // A sample time is 100 ns ticks of the performance counter, as the media clock counts.
    private static MediaTimestamp Timestamp(long hundredNanoseconds)
    {
        MediaTime now = MediaClock.System.Now;
        var captured = new MediaTime(hundredNanoseconds * 100);
        return (now - captured).Duration() < PlausibleSkew
            ? MediaTimestamp.Captured(captured)
            : MediaTimestamp.Observed(now);
    }

    private static int Stride(VideoFormat format) =>
        format.PixelFormat switch
        {
            PixelFormat.Yuy2 or PixelFormat.Uyvy => format.CodedSize.Width * 2,
            PixelFormat.Bgra or PixelFormat.Rgba => format.CodedSize.Width * 4,
            _ => format.CodedSize.Width,
        };

    private void Disconnect(IVideoFrameConsumer consumer)
    {
        lock (_gate)
        {
            _consumers = _consumers.RemoveAll(c => ReferenceEquals(c.Consumer, consumer));
        }

        Reconcile();
    }

    [LoggerMessage(2345, LogLevel.Information, "Opened {Camera} in {Mode}.")]
    private partial void LogOpened(string camera, VideoInputMode mode);

    [LoggerMessage(2346, LogLevel.Error, "Reading {Camera} failed; its capture stops.")]
    private partial void LogReadFailed(Exception exception, string camera);

    [LoggerMessage(2347, LogLevel.Warning, "A consumer failed to take a Media Foundation frame.")]
    private partial void LogConsumerFailed(Exception exception);

    [LoggerMessage(
        2348,
        LogLevel.Information,
        "Reading {Camera} through Direct3D 11 on {Adapter}; frames stay on the GPU."
    )]
    private partial void LogReadingOnGpu(string camera, GpuIdentity adapter);

    [LoggerMessage(
        2349,
        LogLevel.Warning,
        "{Camera} cannot be read through Direct3D 11 on {Adapter}; it is read in memory."
    )]
    private partial void LogGpuReaderFailed(
        Exception exception,
        string camera,
        GpuIdentity adapter
    );

    [LoggerMessage(
        2351,
        LogLevel.Warning,
        "{Camera} did not cancel its pending read in time; its reader is released regardless."
    )]
    private partial void LogFlushTimedOut(string camera);

    [LoggerMessage(2352, LogLevel.Information, "{Camera} ended its stream.")]
    private partial void LogEnded(string camera);

    [LoggerMessage(2350, LogLevel.Debug, "First frame from {Camera}: {Storage} {Format}.")]
    private partial void LogFirstFrame(string camera, VideoStorageKind storage, VideoFormat format);

    // The reader's IMFSourceReaderCallback: a COM object of a vtable and a handle to the input. The reader
    // holds it; the input lets go of its own reference when disposed.
    [StructLayout(LayoutKind.Sequential)]
    private struct ReaderCallback
    {
        private const int NoInterface = unchecked((int)0x80004002);

        private static readonly void** Vtable = CreateVtable();

        private void** _vtable;
        private nint _owner;
        private int _references;

        public static ReaderCallback* Create(MediaFoundationVideoInput owner)
        {
            var callback = (ReaderCallback*)NativeMemory.Alloc((nuint)sizeof(ReaderCallback));
            callback->_vtable = Vtable;
            callback->_owner = GCHandle.ToIntPtr(GCHandle.Alloc(owner));
            callback->_references = 1;
            return callback;
        }

        private static void** CreateVtable()
        {
            void** table = (void**)NativeMemory.Alloc((nuint)(6 * sizeof(void*)));
            table[0] = (delegate* unmanaged<ReaderCallback*, Guid*, void**, int>)&QueryInterface;
            table[1] = (delegate* unmanaged<ReaderCallback*, uint>)&AddRef;
            table[2] = (delegate* unmanaged<ReaderCallback*, uint>)&Release;
            table[3] = (delegate* unmanaged<
                ReaderCallback*,
                int,
                uint,
                uint,
                long,
                IMFSample*,
                int>)
                &OnReadSample;
            table[4] = (delegate* unmanaged<ReaderCallback*, uint, int>)&OnFlush;
            table[5] = (delegate* unmanaged<ReaderCallback*, uint, void*, int>)&OnEvent;
            return table;
        }

        private static MediaFoundationVideoInput Owner(ReaderCallback* self) =>
            (MediaFoundationVideoInput)GCHandle.FromIntPtr(self->_owner).Target!;

        [UnmanagedCallersOnly]
        private static int QueryInterface(ReaderCallback* self, Guid* iid, void** result)
        {
            if (*iid == typeof(IUnknown).GUID || *iid == typeof(IMFSourceReaderCallback).GUID)
            {
                *result = self;
                _ = Interlocked.Increment(ref self->_references);
                return 0;
            }

            *result = null;
            return NoInterface;
        }

        [UnmanagedCallersOnly]
        private static uint AddRef(ReaderCallback* self) =>
            (uint)Interlocked.Increment(ref self->_references);

        [UnmanagedCallersOnly]
        private static uint Release(ReaderCallback* self)
        {
            int left = Interlocked.Decrement(ref self->_references);
            if (left == 0)
            {
                GCHandle.FromIntPtr(self->_owner).Free();
                NativeMemory.Free(self);
            }

            return (uint)left;
        }

        [UnmanagedCallersOnly]
        private static int OnReadSample(
            ReaderCallback* self,
            int status,
            uint stream,
            uint flags,
            long time,
            IMFSample* sample
        )
        {
            MediaFoundationVideoInput owner = Owner(self);
            try
            {
                owner.Answered(status, flags, time, sample);
            }
            catch (Exception exception)
            {
                // Nothing may escape into Media Foundation's thread; no further sample is asked for.
                owner.LogReadFailed(exception, owner.Info.Name);
            }

            return 0;
        }

        [UnmanagedCallersOnly]
        private static int OnFlush(ReaderCallback* self, uint stream)
        {
            Owner(self)._flushed.Set();
            return 0;
        }

        // Stream events are not used.
        [UnmanagedCallersOnly]
        private static int OnEvent(ReaderCallback* self, uint stream, void* mediaEvent) => 0;
    }

    private sealed class Connection(MediaFoundationVideoInput input, IVideoFrameConsumer consumer)
        : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                input.Disconnect(consumer);
            }
        }
    }

    // A kept frame: a hold on its sample, which keeps Media Foundation from reusing the texture.
    private sealed class SampleLease : VideoFrameLease, IVideoFrameRetainer
    {
        // Released leases, for the next frame kept.
        private static readonly ConcurrentBag<SampleLease> Spare = [];

        private IMFSample* _sample;

        private SampleLease(in VideoFrame frame, IMFSample* sample)
            : base(
                frame.Storage,
                frame.Format,
                frame.Timestamp,
                frame.Color,
                frame.Orientation,
                frame.Duration
            )
        {
            _sample = sample;
        }

        // A lease on a frame of the sample, which the caller has already held for it.
        public static SampleLease Of(in VideoFrame frame, IMFSample* sample)
        {
            if (!Spare.TryTake(out SampleLease? lease))
            {
                return new SampleLease(in frame, sample);
            }

            lease._sample = sample;
            lease.Reuse(in frame);
            return lease;
        }

        protected override IVideoFrameRetainer KeepAgain => this;

        public VideoFrameLease Retain(in VideoFrame frame)
        {
            _ = _sample->AddRef();
            return Of(in frame, _sample);
        }

        protected override void Release()
        {
            IMFSample* sample = _sample;
            _sample = null;
            Spare.Add(this);
            _ = sample->Release();
        }
    }
}
