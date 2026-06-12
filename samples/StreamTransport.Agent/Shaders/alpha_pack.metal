// alpha_pack.metal
//
// Side-by-side alpha pack, run as a Syphon.NET SurfaceEffect fragment (compiled against that
// helper's preamble, which provides `VOut { float4 pos; float2 uv; }` with uv in [0,1] and the
// linear clamped sampler `sy_samp`). Output is 2W x H BGRA: the left half is the opaque colour,
// the right half replicates the source alpha to grey. An opaque HEVC encoder's in-ASIC RGB->YUV
// then maps that grey (a,a,a) to luma Y = 16 + 219/255*a, so alpha rides the full-resolution luma
// plane and survives 4:2:0. Byte-identical to the Windows D3D11 (HLSL) and CPU reference packers.

fragment float4 alpha_pack(VOut in [[stage_in]], texture2d<float> src [[texture(0)]]) {
    // [0,0.5) -> colour; [0.5,1) -> the source alpha as grey.
    if (in.uv.x < 0.5) {
        float4 c = src.sample(sy_samp, float2(in.uv.x * 2.0, in.uv.y));
        return float4(c.rgb, 1.0);
    }
    float4 c = src.sample(sy_samp, float2((in.uv.x - 0.5) * 2.0, in.uv.y));
    return float4(c.a, c.a, c.a, 1.0);
}
