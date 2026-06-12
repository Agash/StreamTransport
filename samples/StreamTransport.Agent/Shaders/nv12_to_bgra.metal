// nv12_to_bgra.metal
//
// Full-frame NV12 -> BGRA colour conversion, run as a Syphon.NET SurfaceEffect fragment (uses the
// preamble's `VOut` + `sy_samp`). A hardware HEVC decoder (VideoToolbox) emits an NV12 surface - a
// full-resolution R8 luma plane (texture 0) and a half-resolution RG8 CbCr plane (texture 1) - which
// is the wrong colour if published as-is. Convert to BGRA with the BT.709 limited->full matrix (the
// same constants as alpha_unpack_nv12.metal, just full-width with alpha = 1 and no side-by-side split).
// This is the opaque (non-alpha) receive path's analog of the Windows D3D11Nv12ToBgraConverter.

fragment float4 nv12_to_bgra(VOut in [[stage_in]],
                             texture2d<float> yPlane [[texture(0)]],
                             texture2d<float> uvPlane [[texture(1)]]) {
    float y = yPlane.sample(sy_samp, in.uv).r;
    float2 c = uvPlane.sample(sy_samp, in.uv).rg;
    float Y = (y - 0.0625) * 1.164;
    float U = c.x - 0.5;
    float V = c.y - 0.5;
    float r = saturate(Y + 1.793 * V);
    float g = saturate(Y - 0.213 * U - 0.533 * V);
    float b = saturate(Y + 2.112 * U);
    return float4(r, g, b, 1.0);
}
