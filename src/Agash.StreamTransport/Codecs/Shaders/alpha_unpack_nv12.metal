// alpha_unpack_nv12.metal - inverse of alpha_pack for the common receive case, as a Metal COMPUTE kernel.
// A hardware HEVC decoder (VideoToolbox) emits the decoded 2W x H frame as NV12 - a full-res R8 luma plane
// (texture 0) and a half-res RG8 CbCr plane (texture 1). Reconstruct W x H BGRA: colour from the left half
// via the BT.709 limited->full matrix; alpha from the right-half luma expanded out of the 16..235 limited
// range. Constants match the D3D11/Vulkan unpackers so GPU receivers agree across platforms.
#include <metal_stdlib>
using namespace metal;

kernel void alpha_unpack_nv12(texture2d<float, access::sample> yPlane [[texture(0)]],
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

    // Colour from the left half (normalized x in [0,0.5)).
    float2 cuv = float2(ux * 0.5, uy);
    float y = yPlane.sample(s, cuv).r;
    float2 c = uvPlane.sample(s, cuv).rg;
    float Y = (y - 0.0625) * 1.164;
    float U = c.x - 0.5;
    float V = c.y - 0.5;
    float r = saturate(Y + 1.793 * V);
    float g = saturate(Y - 0.213 * U - 0.533 * V);
    float b = saturate(Y + 2.112 * U);

    // Alpha from the right-half luma, expanded from the 16..235 limited range.
    float ya = yPlane.sample(s, float2(0.5 + ux * 0.5, uy)).r;
    float a = saturate((ya - 0.0625) * 1.164);
    dst.write(float4(r, g, b, a), gid);
}
