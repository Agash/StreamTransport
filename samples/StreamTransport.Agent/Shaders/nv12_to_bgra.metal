// nv12_to_bgra.metal - full-frame NV12 -> BGRA colour conversion as a Metal COMPUTE kernel. A hardware HEVC
// decoder (VideoToolbox) emits an NV12 surface - a full-res R8 luma plane (texture 0) and a half-res RG8 CbCr
// plane (texture 1) - the wrong colour if published as-is. Convert to BGRA with the BT.709 limited->full
// matrix (same constants as alpha_unpack_nv12, full-width, alpha = 1). The opaque receive path's analog of
// the Windows D3D11Nv12ToBgraConverter.
#include <metal_stdlib>
using namespace metal;

kernel void nv12_to_bgra(texture2d<float, access::sample> yPlane [[texture(0)]],
                         texture2d<float, access::sample> uvPlane [[texture(1)]],
                         texture2d<float, access::write> dst [[texture(2)]],
                         uint2 gid [[thread_position_in_grid]]) {
    uint W = dst.get_width();
    uint H = dst.get_height();
    if (gid.x >= W || gid.y >= H) {
        return;
    }
    constexpr sampler s(coord::normalized, address::clamp_to_edge, filter::linear);
    float ux = (float(gid.x) + 0.5) / float(W);
    float uy = (float(gid.y) + 0.5) / float(H);
    float y = yPlane.sample(s, float2(ux, uy)).r;
    float2 c = uvPlane.sample(s, float2(ux, uy)).rg;
    float Y = (y - 0.0625) * 1.164;
    float U = c.x - 0.5;
    float V = c.y - 0.5;
    float r = saturate(Y + 1.793 * V);
    float g = saturate(Y - 0.213 * U - 0.533 * V);
    float b = saturate(Y + 2.112 * U);
    dst.write(float4(r, g, b, 1.0), gid);
}
