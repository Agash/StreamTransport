// alpha_pack.metal - side-by-side alpha pack as a Metal COMPUTE kernel (driven directly by the MS Metal
// bindings; precompiled into the app's default.metallib). Output is 2W x H BGRA: the left half is the
// opaque colour, the right half replicates the source alpha to grey. An opaque HEVC encoder's in-ASIC
// RGB->YUV then maps that grey (a,a,a) to luma Y = 16 + 219/255*a, so alpha rides the full-resolution luma
// plane and survives 4:2:0. Byte-identical to the Windows D3D11 (HLSL), Linux Vulkan (.comp) and CPU packers.
#include <metal_stdlib>
using namespace metal;

kernel void alpha_pack(texture2d<float, access::read> src [[texture(0)]],
                       texture2d<float, access::write> dst [[texture(1)]],
                       uint2 gid [[thread_position_in_grid]]) {
    uint w = src.get_width();   // W (source is W x H; destination is 2W x H)
    uint h = src.get_height();
    if (gid.x >= w * 2 || gid.y >= h) {
        return;
    }
    if (gid.x < w) {
        float4 c = src.read(uint2(gid.x, gid.y));        // left half: opaque colour
        dst.write(float4(c.rgb, 1.0), gid);
    } else {
        float4 c = src.read(uint2(gid.x - w, gid.y));    // right half: alpha replicated to grey
        dst.write(float4(c.a, c.a, c.a, 1.0), gid);
    }
}
