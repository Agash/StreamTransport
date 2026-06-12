// alpha_unpack_nv12.metal
//
// Inverse of alpha_pack for the common receive case: a hardware HEVC decoder (VideoToolbox) emits
// the decoded 2W x H frame as an NV12 surface - a full-resolution R8 luma plane (texture 0) and a
// half-resolution RG8 CbCr plane (texture 1). Reconstruct W x H BGRA with alpha: colour from the
// left half via the BT.709 limited->full matrix; alpha from the right-half luma expanded out of the
// 16..235 limited range. Run as a Syphon.NET SurfaceEffect fragment (uses the preamble's `VOut` and
// `sy_samp`). Constants match the Windows D3D11 unpacker (D3D11AlphaUnpacker) so GPU receivers agree.

fragment float4 alpha_unpack_nv12(VOut in [[stage_in]],
                                  texture2d<float> yPlane [[texture(0)]],
                                  texture2d<float> uvPlane [[texture(1)]]) {
    // Colour from the left half (uv.x in [0,0.5)).
    float2 cuv = float2(in.uv.x * 0.5, in.uv.y);
    float y = yPlane.sample(sy_samp, cuv).r;
    float2 c = uvPlane.sample(sy_samp, cuv).rg;
    float Y = (y - 0.0625) * 1.164;
    float U = c.x - 0.5;
    float V = c.y - 0.5;
    float r = saturate(Y + 1.793 * V);
    float g = saturate(Y - 0.213 * U - 0.533 * V);
    float b = saturate(Y + 2.112 * U);

    // Alpha from the right-half luma (uv.x in [0.5,1)), expanded from the 16..235 limited range.
    float ya = yPlane.sample(sy_samp, float2(0.5 + in.uv.x * 0.5, in.uv.y)).r;
    float a = saturate((ya - 0.0625) * 1.164);
    return float4(r, g, b, a);
}
