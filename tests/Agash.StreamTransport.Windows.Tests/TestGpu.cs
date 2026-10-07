using Agash.StreamTransport.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Security;

namespace Agash.StreamTransport.Windows.Tests;

// A Direct3D 12 device on the default adapter, with what tests need to put pictures on it and read
// results back: textures in COMMON, as frames carry them.
internal sealed unsafe class TestGpu : IDisposable
{
    private readonly ID3D12Device* _device;
    private readonly ID3D12CommandQueue* _queue;
    private readonly ID3D12CommandAllocator* _allocator;
    private readonly ID3D12GraphicsCommandList* _list;
    private readonly ID3D12Fence* _fence;
    private readonly HANDLE _event;
    private readonly List<nint> _textures = [];
    private ulong _submitted;

    private TestGpu(ID3D12Device* device)
    {
        _device = device;
        D3D12_COMMAND_QUEUE_DESC queue = new()
        {
            Type = D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
        };
        _device->CreateCommandQueue(in queue, out ID3D12CommandQueue* created);
        _queue = created;
        _device->CreateCommandAllocator(
            D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            out ID3D12CommandAllocator* allocator
        );
        _allocator = allocator;
        _device->CreateCommandList(
            0,
            D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            allocator,
            null,
            out ID3D12GraphicsCommandList* list
        );
        _list = list;
        _list->Close();
        _device->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, out ID3D12Fence* fence);
        _fence = fence;
        _event = TestWin32.CreateEvent((SECURITY_ATTRIBUTES*)null, false, false, default(PCWSTR));
        LUID luid = _device->GetAdapterLuid();
        Adapter = GpuIdentity.FromAdapterLuid(((ulong)(uint)luid.HighPart << 32) | luid.LowPart);
    }

    public GpuIdentity Adapter { get; }

    /// <summary>The ID3D12Device.</summary>
    public nint Device => (nint)_device;

    public static TestGpu Open()
    {
        Guid iid = typeof(ID3D12Device).GUID;
        void* device;
        HRESULT result = TestWin32.D3D12CreateDevice(
            null,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
            &iid,
            &device
        );
        if (result.Failed)
        {
            Assert.Inconclusive($"No Direct3D 12 device: 0x{result.Value:X8}.");
        }

        return new TestGpu((ID3D12Device*)device);
    }

    // A texture holding the given planes, each tightly packed row by row.
    public nint Upload(DXGI_FORMAT format, int width, int height, params byte[][] planes)
    {
        nint texture = CreateTexture(format, width, height);
        using Staging staging = Stage(
            texture,
            planes.Length,
            D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD
        );
        for (int plane = 0; plane < planes.Length; plane++)
        {
            staging.Write(plane, planes[plane]);
        }

        Record(
            texture,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
            staging,
            toTexture: true
        );
        return texture;
    }

    // The planes of a texture a frame carries, after the frame's producer is done with it.
    public byte[][] Download(D3D12Image image, int planes)
    {
        if (image.Sync.Fence != 0)
        {
            _queue->Wait((ID3D12Fence*)image.Sync.Fence, image.Sync.Value);
        }

        using Staging staging = Stage(
            image.Resource,
            planes,
            D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK
        );
        Record(
            image.Resource,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE,
            staging,
            toTexture: false
        );
        return [.. Enumerable.Range(0, planes).Select(staging.Read)];
    }

    public void Dispose()
    {
        Flush();
        foreach (nint texture in _textures)
        {
            _ = ((ID3D12Resource*)texture)->Release();
        }

        _ = _list->Release();
        _ = _allocator->Release();
        _ = _fence->Release();
        _ = _queue->Release();
        _ = TestWin32.CloseHandle(_event);
        _ = _device->Release();
    }

    private nint CreateTexture(DXGI_FORMAT format, int width, int height)
    {
        D3D12_HEAP_PROPERTIES heap = new() { Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT };
        D3D12_RESOURCE_DESC description = Texture(format, width, height);
        Guid iid = typeof(ID3D12Resource).GUID;
        void* resource;
        _device->CreateCommittedResource(
            &heap,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            &description,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
            null,
            &iid,
            &resource
        );
        _textures.Add((nint)resource);
        return (nint)resource;
    }

    private static D3D12_RESOURCE_DESC Texture(DXGI_FORMAT format, int width, int height) =>
        new()
        {
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            Width = (ulong)width,
            Height = (uint)height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
        };

    private Staging Stage(nint texture, int planes, D3D12_HEAP_TYPE type)
    {
        D3D12_RESOURCE_DESC description = ((ID3D12Resource*)texture)->GetDesc();
        var footprints = new D3D12_PLACED_SUBRESOURCE_FOOTPRINT[planes];
        uint[] rows = new uint[planes];
        ulong[] rowBytes = new ulong[planes];
        ulong total;
        fixed (D3D12_PLACED_SUBRESOURCE_FOOTPRINT* f = footprints)
        fixed (uint* r = rows)
        fixed (ulong* b = rowBytes)
        {
            _device->GetCopyableFootprints(&description, 0, (uint)planes, 0, f, r, b, &total);
        }

        D3D12_HEAP_PROPERTIES heap = new() { Type = type };
        D3D12_RESOURCE_DESC buffer = new()
        {
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
            Width = total,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
        };
        Guid iid = typeof(ID3D12Resource).GUID;
        void* resource;
        _device->CreateCommittedResource(
            &heap,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            &buffer,
            type == D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD
                ? D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ
                : D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
            null,
            &iid,
            &resource
        );
        return new Staging((ID3D12Resource*)resource, footprints, rows, rowBytes);
    }

    private void Record(
        nint texture,
        D3D12_RESOURCE_STATES copying,
        Staging staging,
        bool toTexture
    )
    {
        _allocator->Reset();
        _list->Reset(_allocator, null);
        Transition(texture, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, copying);
        for (int plane = 0; plane < staging.Planes; plane++)
        {
            D3D12_TEXTURE_COPY_LOCATION subresource = new()
            {
                pResource = (ID3D12Resource*)texture,
                Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
            };
            subresource.Anonymous.SubresourceIndex = (uint)plane;
            D3D12_TEXTURE_COPY_LOCATION placed = new()
            {
                pResource = staging.Buffer,
                Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
            };
            placed.Anonymous.PlacedFootprint = staging.Footprint(plane);
            if (toTexture)
            {
                _list->CopyTextureRegion(&subresource, 0, 0, 0, &placed, null);
            }
            else
            {
                _list->CopyTextureRegion(&placed, 0, 0, 0, &subresource, null);
            }
        }

        Transition(texture, copying, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON);
        _list->Close();
        var list = (ID3D12CommandList*)_list;
        _queue->ExecuteCommandLists(1, &list);
        Flush();
    }

    private void Transition(
        nint resource,
        D3D12_RESOURCE_STATES before,
        D3D12_RESOURCE_STATES after
    )
    {
        D3D12_RESOURCE_BARRIER barrier = new()
        {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        };
        barrier.Anonymous.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER
        {
            pResource = (ID3D12Resource*)resource,
            Subresource = 0xFFFF_FFFF,
            StateBefore = before,
            StateAfter = after,
        };
        _list->ResourceBarrier(1, &barrier);
    }

    private void Flush()
    {
        _queue->Signal(_fence, ++_submitted);
        if (_fence->GetCompletedValue() < _submitted)
        {
            _fence->SetEventOnCompletion(_submitted, _event);
            _ = TestWin32.WaitForSingleObject(_event, TestWin32.INFINITE);
        }
    }

    // An upload or readback buffer laid out as the texture's copyable footprints.
    private sealed class Staging(
        ID3D12Resource* buffer,
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT[] footprints,
        uint[] rows,
        ulong[] rowBytes
    ) : IDisposable
    {
        public ID3D12Resource* Buffer => buffer;

        public int Planes => footprints.Length;

        public D3D12_PLACED_SUBRESOURCE_FOOTPRINT Footprint(int plane) => footprints[plane];

        public void Write(int plane, byte[] data)
        {
            byte* mapped;
            buffer->Map(0, null, (void**)&mapped);
            int rowLength = (int)rowBytes[plane];
            for (int row = 0; row < rows[plane]; row++)
            {
                data.AsSpan(row * rowLength, rowLength)
                    .CopyTo(
                        new Span<byte>(
                            mapped
                                + footprints[plane].Offset
                                + (row * footprints[plane].Footprint.RowPitch),
                            rowLength
                        )
                    );
            }

            buffer->Unmap(0, null);
        }

        public byte[] Read(int plane)
        {
            int rowLength = (int)rowBytes[plane];
            byte[] data = new byte[rowLength * rows[plane]];
            byte* mapped;
            buffer->Map(0, null, (void**)&mapped);
            for (int row = 0; row < rows[plane]; row++)
            {
                new ReadOnlySpan<byte>(
                    mapped
                        + footprints[plane].Offset
                        + (row * footprints[plane].Footprint.RowPitch),
                    rowLength
                ).CopyTo(data.AsSpan(row * rowLength));
            }

            buffer->Unmap(0, null);
            return data;
        }

        public void Dispose() => _ = buffer->Release();
    }
}
