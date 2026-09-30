// 4:2:0 Y'CbCr planes to RGB. With Unpack set, the input is a side-by-side frame twice the output
// width: colour from the left half, and alpha from the right half's luma, normalised like the colour's.

cbuffer Parameters : register(b0)
{
    uint OutWidth;
    uint OutHeight;
    uint Unpack;        // 1: alpha comes from the right half's luma
    uint Padding;
    float4 LumaScale;   // x: offset subtracted from Y', y: scale after it, z: neutral chroma
    float4 RowR;        // R = dot(float3(Y, Cb, Cr), RowR.xyz) + RowR.w, on normalised Y, Cb, Cr
    float4 RowG;
    float4 RowB;
};

Texture2DArray<float> Luma : register(t0);
Texture2DArray<float2> Chroma : register(t1);
RWTexture2D<float4> Output : register(u0);

[numthreads(8, 8, 1)]
void Main(uint3 id : SV_DispatchThreadID)
{
    uint2 p = id.xy;
    if (p.x >= OutWidth || p.y >= OutHeight)
    {
        return;
    }

    float y = (Luma.Load(int4(p, 0, 0)) - LumaScale.x) * LumaScale.y;
    float2 cbcr = Chroma.Load(int4(p / 2, 0, 0)) - LumaScale.z;
    float3 ycc = float3(y, cbcr);
    float3 rgb = saturate(float3(
        dot(ycc, RowR.xyz) + RowR.w,
        dot(ycc, RowG.xyz) + RowG.w,
        dot(ycc, RowB.xyz) + RowB.w));

    float alpha = 1;
    if (Unpack != 0)
    {
        alpha = saturate((Luma.Load(int4(p.x + OutWidth, p.y, 0, 0)) - LumaScale.x) * LumaScale.y);
    }

    Output[p] = float4(rgb, alpha);
}
