// alpha_unpack_bgra.metal
//
// Inverse of alpha_pack when the decoded 2W x H frame arrives as BGRA rather than NV12 (e.g. a
// colour-converted decode). Colour comes straight from the left half; alpha from the right-half
// grey, expanded from the same 16..235 limited range used on pack so it matches the NV12 path and
// stays interchange-compatible. Run as a Syphon.NET SurfaceEffect fragment (uses `VOut`/`sy_samp`).

fragment float4 alpha_unpack_bgra(VOut in [[stage_in]], texture2d<float> src [[texture(0)]]) {
    float3 rgb = src.sample(sy_samp, float2(in.uv.x * 0.5, in.uv.y)).rgb;
    float ya = src.sample(sy_samp, float2(0.5 + in.uv.x * 0.5, in.uv.y)).r;
    float a = saturate((ya - 0.0625) * 1.164);
    return float4(rgb, a);
}
