// alpha_unpack_bgra.metal - inverse of alpha_pack when the decoded 2W x H frame arrives as BGRA (a
// colour-converted decode) rather than NV12, as a Metal COMPUTE kernel. Colour from the left half; alpha
// from the right-half grey, expanded from the same 16..235 limited range used on pack so it matches the
// NV12 path and stays interchange-compatible.
#include <metal_stdlib>
using namespace metal;

kernel void alpha_unpack_bgra(texture2d<float, access::sample> src [[texture(0)]],
                              texture2d<float, access::write> dst [[texture(1)]],
                              uint2 gid [[thread_position_in_grid]]) {
    uint W = dst.get_width();
    uint H = dst.get_height();
    if (gid.x >= W || gid.y >= H) {
        return;
    }
    constexpr sampler s(coord::normalized, address::clamp_to_edge, filter::linear);
    float ux = (float(gid.x) + 0.5) / float(W);
    float uy = (float(gid.y) + 0.5) / float(H);
    float3 rgb = src.sample(s, float2(ux * 0.5, uy)).rgb;
    float ya = src.sample(s, float2(0.5 + ux * 0.5, uy)).r;
    float a = saturate((ya - 0.0625) * 1.164);
    dst.write(float4(rgb, a), gid);
}
