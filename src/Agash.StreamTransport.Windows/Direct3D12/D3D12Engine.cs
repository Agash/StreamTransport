using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Security;

namespace Agash.StreamTransport.Windows.Direct3D12;

/// <summary>The compute shaders the engine runs.</summary>
internal enum D3D12Shader
{
    /// <summary>RGB to NV12 planes, optionally packing alpha side by side.</summary>
    RgbToYuv,

    /// <summary>NV12 planes to RGB, optionally unpacking side-by-side alpha.</summary>
    YuvToRgb,
}

/// <summary>
/// Compute work on one Direct3D 12 device: a compute queue with a fence, a ring of command allocators and
/// descriptor slots reused once the fence shows their work finished, and one root signature for both
/// shaders (root constants at b0, two SRVs, two UAVs, a linear clamp sampler at s0). It is counted:
/// frames it produced carry its fence, so whatever holds such frames holds the engine too, and the
/// last holder's release destroys it.
/// </summary>
internal sealed unsafe class D3D12Engine : IDisposable
{
    public const int RootConstants = 20;
    private const int Ring = 8;
    private const int DescriptorsPerSlot = 4;
    private const uint AllSubresources = 0xFFFF_FFFF;

    private readonly ID3D12Device* _device;
    private readonly ID3D12CommandQueue* _queue;
    private readonly ID3D12Fence* _fence;
    private readonly ID3D12Fence* _ordering;
    private readonly HANDLE _event;
    private readonly ID3D12CommandAllocator*[] _allocators = new ID3D12CommandAllocator*[Ring];
    private readonly ulong[] _slotDone = new ulong[Ring];
    private readonly ID3D12GraphicsCommandList* _list;
    private readonly ID3D12DescriptorHeap* _heap;
    private readonly uint _descriptorSize;
    private readonly ID3D12RootSignature* _rootSignature;
    private readonly ID3D12PipelineState* _rgbToYuv;
    private readonly ID3D12PipelineState* _yuvToRgb;
    private ulong _ordered;
    private int _holders = 1;
    private int _slot = -1;

    /// <summary>An engine on a device, which it adds a reference to.</summary>
    /// <param name="device">The ID3D12Device.</param>
    /// <param name="direct">
    /// Run on a direct queue, which can also be handed to libraries that need one; a compute queue
    /// otherwise.
    /// </param>
    public D3D12Engine(nint device, bool direct = false)
    {
        D3D12_COMMAND_LIST_TYPE type = direct
            ? D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT
            : D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_COMPUTE;
        _device = (ID3D12Device*)device;
        _ = _device->AddRef();
        Adapter = GpuIdentityOf(_device);

        D3D12_COMMAND_QUEUE_DESC queue = new() { Type = type };
        _device->CreateCommandQueue(in queue, out ID3D12CommandQueue* created);
        _queue = created;
        _device->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, out ID3D12Fence* fence);
        _fence = fence;
        _device->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, out ID3D12Fence* ordering);
        _ordering = ordering;
        _event = Win32.CreateEvent((SECURITY_ATTRIBUTES*)null, false, false, default(PCWSTR));
        for (int i = 0; i < Ring; i++)
        {
            _device->CreateCommandAllocator(type, out ID3D12CommandAllocator* allocator);
            _allocators[i] = allocator;
        }

        _device->CreateCommandList(
            0,
            type,
            _allocators[0],
            null,
            out ID3D12GraphicsCommandList* list
        );
        _list = list;
        _list->Close();

        D3D12_DESCRIPTOR_HEAP_DESC heap = new()
        {
            Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
            NumDescriptors = Ring * DescriptorsPerSlot,
            Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE,
        };
        _device->CreateDescriptorHeap(in heap, out ID3D12DescriptorHeap* descriptors);
        _heap = descriptors;
        _descriptorSize = _device->GetDescriptorHandleIncrementSize(
            D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV
        );

        _rootSignature = CreateRootSignature();
        _rgbToYuv = CreatePipeline("RgbToYuv.cso");
        _yuvToRgb = CreatePipeline("YuvToRgb.cso");
    }

    /// <summary>The device, borrowed.</summary>
    public nint Device => (nint)_device;

    /// <summary>The engine's command queue, borrowed.</summary>
    public nint Queue => (nint)_queue;

    /// <summary>The adapter the device is on.</summary>
    public Media.GpuIdentity Adapter { get; }

    /// <summary>The fence the engine's queue signals as its work completes.</summary>
    public nint Fence => (nint)_fence;

    /// <summary>The fence value of the last submitted work.</summary>
    public ulong Submitted { get; private set; }

    /// <summary>The fence value the engine's queue has reached.</summary>
    public ulong Completed => _fence->GetCompletedValue();

    /// <summary>The device a resource was made on, with a reference the caller releases.</summary>
    /// <param name="resource">The ID3D12Resource.</param>
    /// <returns>The ID3D12Device.</returns>
    public static nint DeviceOf(nint resource)
    {
        var child = (ID3D12DeviceChild*)resource;
        Guid iid = typeof(ID3D12Device).GUID;
        void* device;
        child->GetDevice(&iid, &device);
        return (nint)device;
    }

    /// <summary>Releases a COM reference.</summary>
    /// <param name="unknown">The object.</param>
    public static void Release(nint unknown)
    {
        if (unknown != 0)
        {
            _ = ((ID3D12DeviceChild*)unknown)->Release();
        }
    }

    /// <summary>A texture's description.</summary>
    /// <param name="resource">The ID3D12Resource.</param>
    /// <returns>The description.</returns>
    public static D3D12_RESOURCE_DESC Describe(nint resource) =>
        ((ID3D12Resource*)resource)->GetDesc();

    /// <summary>Whether shaders can store to a format through a typed UAV on this device.</summary>
    /// <param name="format">The format.</param>
    /// <returns>True when a typed UAV store works.</returns>
    public bool CanStore(DXGI_FORMAT format)
    {
        D3D12_FEATURE_DATA_FORMAT_SUPPORT support = new() { Format = format };
        try
        {
            _device->CheckFeatureSupport(
                D3D12_FEATURE.D3D12_FEATURE_FORMAT_SUPPORT,
                &support,
                (uint)sizeof(D3D12_FEATURE_DATA_FORMAT_SUPPORT)
            );
        }
        catch (COMException)
        {
            // Deliberately not logged: a format the device does not know cannot be stored to.
            return false;
        }

        return support.Support2.HasFlag(
            D3D12_FORMAT_SUPPORT2.D3D12_FORMAT_SUPPORT2_UAV_TYPED_STORE
        );
    }

    /// <summary>Makes a 2D texture with one mip level.</summary>
    /// <param name="format">The format.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="unorderedAccess">Whether shaders write it.</param>
    /// <param name="initial">The state it starts in.</param>
    /// <returns>The ID3D12Resource, owned by the caller.</returns>
    public nint CreateTexture(
        DXGI_FORMAT format,
        int width,
        int height,
        bool unorderedAccess,
        D3D12_RESOURCE_STATES initial
    )
    {
        D3D12_HEAP_PROPERTIES heap = new() { Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT };
        D3D12_RESOURCE_DESC description = new()
        {
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            Width = (ulong)width,
            Height = (uint)height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Flags = unorderedAccess
                ? D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS
                : D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_NONE,
        };
        Guid iid = typeof(ID3D12Resource).GUID;
        void* resource;
        _device->CreateCommittedResource(
            &heap,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            &description,
            initial,
            null,
            &iid,
            &resource
        );
        return (nint)resource;
    }

    /// <summary>
    /// Opens a command list for the next submission: waits until the ring slot's last work finished,
    /// then resets its allocator and binds the heap, root signature and pipeline.
    /// </summary>
    /// <param name="shader">The shader the submission runs.</param>
    /// <returns>The command list, open.</returns>
    public ID3D12GraphicsCommandList* Begin(D3D12Shader shader)
    {
        ID3D12GraphicsCommandList* list = Open(
            shader == D3D12Shader.RgbToYuv ? _rgbToYuv : _yuvToRgb
        );
        ID3D12DescriptorHeap* heap = _heap;
        list->SetDescriptorHeaps(1, &heap);
        list->SetComputeRootSignature(_rootSignature);
        list->SetComputeRootDescriptorTable(1, GpuHandle(0));
        list->SetComputeRootDescriptorTable(2, GpuHandle(2));
        return list;
    }

    /// <summary>A buffer in the upload heap, which the CPU writes and the GPU copies from.</summary>
    /// <param name="size">Its size in bytes.</param>
    /// <returns>The ID3D12Resource, owned by the caller.</returns>
    public nint CreateUploadBuffer(ulong size) => CreateBuffer(size, readback: false);

    /// <summary>A buffer in the readback heap, which the GPU copies into and the CPU reads.</summary>
    /// <param name="size">Its size in bytes.</param>
    /// <returns>The ID3D12Resource, owned by the caller.</returns>
    public nint CreateReadbackBuffer(ulong size) => CreateBuffer(size, readback: true);

    private nint CreateBuffer(ulong size, bool readback)
    {
        D3D12_HEAP_PROPERTIES heap = new()
        {
            Type = readback
                ? D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK
                : D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD,
        };
        D3D12_RESOURCE_DESC description = new()
        {
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
            Width = size,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
        };
        Guid iid = typeof(ID3D12Resource).GUID;
        void* resource;
        _device->CreateCommittedResource(
            &heap,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            &description,
            readback
                ? D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST
                : D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ,
            null,
            &iid,
            &resource
        );
        return (nint)resource;
    }

    /// <summary>How a texture's planes lie in a buffer copied to or from: offsets and row pitches.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="footprints">One per plane, filled in.</param>
    /// <param name="rows">Each plane's row count, filled in.</param>
    /// <returns>The bytes the buffer needs.</returns>
    public ulong Footprints(
        nint texture,
        Span<D3D12_PLACED_SUBRESOURCE_FOOTPRINT> footprints,
        Span<uint> rows
    )
    {
        D3D12_RESOURCE_DESC description = Describe(texture);
        ulong total;
        fixed (D3D12_PLACED_SUBRESOURCE_FOOTPRINT* placed = footprints)
        fixed (uint* count = rows)
        {
            _device->GetCopyableFootprints(
                &description,
                0,
                (uint)footprints.Length,
                0,
                placed,
                count,
                null,
                &total
            );
        }

        return total;
    }

    /// <summary>Records a copy of a texture's plane into a buffer at its footprint.</summary>
    /// <param name="list">The open command list.</param>
    /// <param name="texture">The texture, readable by copies.</param>
    /// <param name="plane">The plane's subresource.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="footprint">Where the plane goes in it.</param>
    public static void CopyToBuffer(
        ID3D12GraphicsCommandList* list,
        nint texture,
        uint plane,
        nint buffer,
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint
    )
    {
        D3D12_TEXTURE_COPY_LOCATION to = new()
        {
            pResource = (ID3D12Resource*)buffer,
            Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
        };
        to.Anonymous.PlacedFootprint = footprint;
        D3D12_TEXTURE_COPY_LOCATION from = new()
        {
            pResource = (ID3D12Resource*)texture,
            Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
        };
        from.Anonymous.SubresourceIndex = plane;
        list->CopyTextureRegion(&to, 0, 0, 0, &from, null);
    }

    /// <summary>How a texture's first subresource lies in a buffer it is copied from: its row pitch.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="size">The bytes the buffer needs.</param>
    /// <returns>The placed footprint.</returns>
    public D3D12_PLACED_SUBRESOURCE_FOOTPRINT Footprint(nint texture, out ulong size)
    {
        D3D12_RESOURCE_DESC description = Describe(texture);
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint;
        ulong total;
        _device->GetCopyableFootprints(&description, 0, 1, 0, &footprint, null, null, &total);
        size = total;
        return footprint;
    }

    /// <summary>Records a copy of a buffer laid out as a footprint into a texture's first subresource.</summary>
    /// <param name="list">The open command list.</param>
    /// <param name="texture">The texture, in the copy-destination state.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="footprint">How the texture lies in the buffer.</param>
    public static void CopyFromBuffer(
        ID3D12GraphicsCommandList* list,
        nint texture,
        nint buffer,
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint
    )
    {
        D3D12_TEXTURE_COPY_LOCATION to = new()
        {
            pResource = (ID3D12Resource*)texture,
            Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
        };
        to.Anonymous.SubresourceIndex = 0;
        D3D12_TEXTURE_COPY_LOCATION from = new()
        {
            pResource = (ID3D12Resource*)buffer,
            Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
        };
        from.Anonymous.PlacedFootprint = footprint;
        list->CopyTextureRegion(&to, 0, 0, 0, &from, null);
    }

    /// <summary>Opens a command list for copies alone.</summary>
    /// <returns>The command list, open.</returns>
    public ID3D12GraphicsCommandList* BeginCopy() => Open(null);

    // The next ring slot's allocator, once its last work finished, with the list reset onto it.
    private ID3D12GraphicsCommandList* Open(ID3D12PipelineState* pipeline)
    {
        _slot = (_slot + 1) % Ring;
        WaitFor(_slotDone[_slot]);
        ID3D12CommandAllocator* allocator = _allocators[_slot];
        allocator->Reset();
        _list->Reset(allocator, pipeline);
        return _list;
    }

    /// <summary>Writes a shader resource view into the open slot.</summary>
    /// <param name="index">0 or 1, for t0 or t1.</param>
    /// <param name="resource">The texture, or zero for a null view.</param>
    /// <param name="format">The view format.</param>
    /// <param name="arraySlice">The array slice.</param>
    /// <param name="plane">The plane.</param>
    public void ShaderResource(
        int index,
        nint resource,
        DXGI_FORMAT format,
        uint arraySlice,
        uint plane
    )
    {
        D3D12_SHADER_RESOURCE_VIEW_DESC view = new()
        {
            Format = format,
            ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2DARRAY,
            Shader4ComponentMapping = 0x1688, // the default: each component from itself
        };
        view.Anonymous.Texture2DArray = new D3D12_TEX2D_ARRAY_SRV
        {
            MipLevels = 1,
            FirstArraySlice = arraySlice,
            ArraySize = 1,
            PlaneSlice = plane,
        };
        _device->CreateShaderResourceView((ID3D12Resource*)resource, &view, CpuHandle(index));
    }

    /// <summary>Writes an unordered access view into the open slot.</summary>
    /// <param name="index">0 or 1, for u0 or u1.</param>
    /// <param name="resource">The texture, or zero for a null view.</param>
    /// <param name="format">The view format.</param>
    public void UnorderedAccess(int index, nint resource, DXGI_FORMAT format)
    {
        D3D12_UNORDERED_ACCESS_VIEW_DESC view = new()
        {
            Format = format,
            ViewDimension = D3D12_UAV_DIMENSION.D3D12_UAV_DIMENSION_TEXTURE2D,
        };
        _device->CreateUnorderedAccessView(
            (ID3D12Resource*)resource,
            null,
            &view,
            CpuHandle(2 + index)
        );
    }

    /// <summary>Records a transition of a whole resource.</summary>
    /// <param name="list">The open command list.</param>
    /// <param name="resource">The resource.</param>
    /// <param name="before">Its state now.</param>
    /// <param name="after">The state it goes to.</param>
    public static void Transition(
        ID3D12GraphicsCommandList* list,
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
            Subresource = AllSubresources,
            StateBefore = before,
            StateAfter = after,
        };
        list->ResourceBarrier(1, &barrier);
    }

    /// <summary>Records a copy of one subresource to another.</summary>
    /// <param name="list">The open command list.</param>
    /// <param name="destination">The destination texture.</param>
    /// <param name="destinationSubresource">Its subresource.</param>
    /// <param name="source">The source texture.</param>
    /// <param name="sourceSubresource">Its subresource.</param>
    public static void Copy(
        ID3D12GraphicsCommandList* list,
        nint destination,
        uint destinationSubresource,
        nint source,
        uint sourceSubresource
    )
    {
        D3D12_TEXTURE_COPY_LOCATION to = new()
        {
            pResource = (ID3D12Resource*)destination,
            Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
        };
        to.Anonymous.SubresourceIndex = destinationSubresource;
        D3D12_TEXTURE_COPY_LOCATION from = new()
        {
            pResource = (ID3D12Resource*)source,
            Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
        };
        from.Anonymous.SubresourceIndex = sourceSubresource;
        list->CopyTextureRegion(&to, 0, 0, 0, &from, null);
    }

    /// <summary>Makes the engine's queue wait for a producer: a fence value, or its queue's work so far.</summary>
    /// <param name="sync">The producer's synchronisation.</param>
    public void WaitForProducer(Media.D3D12Sync sync)
    {
        if (sync.Fence != 0)
        {
            _queue->Wait((ID3D12Fence*)sync.Fence, sync.Value);
        }
        else if (sync.ProducerQueue != 0)
        {
            // The producer's queue signals a fence of ours after its submitted work; ours waits for it.
            ((ID3D12CommandQueue*)sync.ProducerQueue)->Signal(_ordering, ++_ordered);
            _queue->Wait(_ordering, _ordered);
        }
    }

    /// <summary>Closes and submits the open command list; the fence reaches the returned value when it is done.</summary>
    /// <returns>The fence value of the submission.</returns>
    public ulong Submit()
    {
        _list->Close();
        var list = (ID3D12CommandList*)_list;
        _queue->ExecuteCommandLists(1, &list);
        _queue->Signal(_fence, ++Submitted);
        _slotDone[_slot] = Submitted;
        return Submitted;
    }

    /// <summary>Blocks until the fence reaches a value.</summary>
    /// <param name="value">The value.</param>
    public void WaitFor(ulong value)
    {
        if (_fence->GetCompletedValue() < value)
        {
            _fence->SetEventOnCompletion(value, _event);
            _ = Win32.WaitForSingleObject(_event, Win32.INFINITE);
        }
    }

    /// <summary>Adds a holder.</summary>
    /// <returns>The engine.</returns>
    public D3D12Engine Hold()
    {
        _ = Interlocked.Increment(ref _holders);
        return this;
    }

    /// <summary>Releases a holder; the last one destroys the engine once its work is done.</summary>
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _holders) != 0)
        {
            return;
        }

        WaitFor(Submitted);
        _ = _rgbToYuv->Release();
        _ = _yuvToRgb->Release();
        _ = _rootSignature->Release();
        _ = _heap->Release();
        _ = _list->Release();
        foreach (ID3D12CommandAllocator* allocator in _allocators)
        {
            _ = allocator->Release();
        }

        _ = _ordering->Release();
        _ = _fence->Release();
        _ = _queue->Release();
        _ = Win32.CloseHandle(_event);
        _ = _device->Release();
    }

    private D3D12_CPU_DESCRIPTOR_HANDLE CpuHandle(int index) =>
        new()
        {
            ptr =
                _heap->GetCPUDescriptorHandleForHeapStart().ptr
                + (nuint)(((_slot * DescriptorsPerSlot) + index) * _descriptorSize),
        };

    private D3D12_GPU_DESCRIPTOR_HANDLE GpuHandle(int index) =>
        new()
        {
            ptr =
                _heap->GetGPUDescriptorHandleForHeapStart().ptr
                + (ulong)(((_slot * DescriptorsPerSlot) + index) * _descriptorSize),
        };

    private ID3D12RootSignature* CreateRootSignature()
    {
        D3D12_DESCRIPTOR_RANGE shaderResources = new()
        {
            RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV,
            NumDescriptors = 2,
        };
        D3D12_DESCRIPTOR_RANGE unorderedAccess = new()
        {
            RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_UAV,
            NumDescriptors = 2,
        };
        D3D12_ROOT_PARAMETER* parameters = stackalloc D3D12_ROOT_PARAMETER[3];
        parameters[0] = new D3D12_ROOT_PARAMETER
        {
            ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS,
        };
        parameters[0].Anonymous.Constants = new D3D12_ROOT_CONSTANTS
        {
            Num32BitValues = RootConstants,
        };
        parameters[1] = new D3D12_ROOT_PARAMETER
        {
            ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE,
        };
        parameters[1].Anonymous.DescriptorTable = new D3D12_ROOT_DESCRIPTOR_TABLE
        {
            NumDescriptorRanges = 1,
            pDescriptorRanges = &shaderResources,
        };
        parameters[2] = new D3D12_ROOT_PARAMETER
        {
            ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE,
        };
        parameters[2].Anonymous.DescriptorTable = new D3D12_ROOT_DESCRIPTOR_TABLE
        {
            NumDescriptorRanges = 1,
            pDescriptorRanges = &unorderedAccess,
        };
        D3D12_STATIC_SAMPLER_DESC sampler = new()
        {
            Filter = D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressV = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            MaxLOD = float.MaxValue,
            ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL,
        };
        D3D12_ROOT_SIGNATURE_DESC description = new()
        {
            NumParameters = 3,
            pParameters = parameters,
            NumStaticSamplers = 1,
            pStaticSamplers = &sampler,
        };

        ID3DBlob* blob;
        ID3DBlob* error;
        HRESULT result = Win32.D3D12SerializeRootSignature(
            &description,
            D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1,
            &blob,
            &error
        );
        if (error is not null)
        {
            _ = error->Release();
        }

        result.ThrowOnFailure();
        try
        {
            Guid iid = typeof(ID3D12RootSignature).GUID;
            void* signature;
            _device->CreateRootSignature(
                0,
                blob->GetBufferPointer(),
                blob->GetBufferSize(),
                &iid,
                &signature
            );
            return (ID3D12RootSignature*)signature;
        }
        finally
        {
            _ = blob->Release();
        }
    }

    private ID3D12PipelineState* CreatePipeline(string shader)
    {
        using Stream stream =
            typeof(D3D12Engine).Assembly.GetManifestResourceStream(shader)
            ?? throw new InvalidOperationException($"The shader {shader} is not embedded.");
        byte[] bytecode = new byte[stream.Length];
        stream.ReadExactly(bytecode);
        fixed (byte* code = bytecode)
        {
            D3D12_COMPUTE_PIPELINE_STATE_DESC description = new()
            {
                pRootSignature = _rootSignature,
                CS = new D3D12_SHADER_BYTECODE
                {
                    pShaderBytecode = code,
                    BytecodeLength = (nuint)bytecode.Length,
                },
            };
            Guid iid = typeof(ID3D12PipelineState).GUID;
            void* pipeline;
            _device->CreateComputePipelineState(&description, &iid, &pipeline);
            return (ID3D12PipelineState*)pipeline;
        }
    }

    private static Media.GpuIdentity GpuIdentityOf(ID3D12Device* device)
    {
        LUID luid = device->GetAdapterLuid();
        return Media.GpuIdentity.FromAdapterLuid(((ulong)(uint)luid.HighPart << 32) | luid.LowPart);
    }
}
