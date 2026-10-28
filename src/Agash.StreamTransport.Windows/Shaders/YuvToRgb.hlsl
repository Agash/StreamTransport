// 4:2:0 Y'CbCr planes to RGB. With Unpack 1 the input is a side-by-side frame twice the output width:
// colour from the left half, and alpha from the right half's luma, normalised like the colour's. With
// Unpack 2 it is a stacked frame twice the output height, the alpha in the bottom half.

cbuffer Parameters : register(b0)
{
    uint OutWidth;
    uint OutHeight;
    uint Unpack;        // 1: alpha comes from the right half's luma; 2: from the bottom half's
    uint Padding;
    float4 LumaScale;   // x: offset subtracted from Y', y: scale after it, z: neutral chroma
    float4 RowR;        // R = dot(float3(Y, Cb, Cr), RowR.xyz) + RowR.w, on normalised Y, Cb, Cr
    float4 RowG;
    float4 RowB;
};

// Chroma is left-sited (H.264, H.265 and AV1's default): a sample sits on every other column, between two
// rows. So an even column takes it as it is and an odd one the mean of its two neighbours, and each row
// weighs the chroma row it falls in 3:1 against the next nearest.

Texture2DArray<float> Luma : register(t0);
Texture2DArray<float2> Chroma : register(t1);
RWTexture2D<float4> Output : register(u0);

// The chroma for luma column x on chroma row r, interpolated across columns.
float2 ChromaRow(uint x, uint r, uint last)
{
    uint c = x / 2;
    float2 here = Chroma.Load(int4(c, r, 0, 0));
    return (x & 1) == 0 ? here : (here + Chroma.Load(int4(min(c + 1, last), r, 0, 0))) * 0.5;
}

[numthreads(8, 8, 1)]
void Main(uint3 id : SV_DispatchThreadID)
{
    uint2 p = id.xy;
    if (p.x >= OutWidth || p.y >= OutHeight)
    {
        return;
    }

    float y = (Luma.Load(int4(p, 0, 0)) - LumaScale.x) * LumaScale.y;
    uint lastColumn = (OutWidth + 1) / 2 - 1;
    uint row = p.y / 2;
    uint other = (p.y & 1) == 0 ? max(row, 1u) - 1 : min(row + 1, (OutHeight + 1) / 2 - 1);
    float2 cbcr = ChromaRow(p.x, row, lastColumn) * 0.75 + ChromaRow(p.x, other, lastColumn) * 0.25
        - LumaScale.z;
    float3 ycc = float3(y, cbcr);
    float3 rgb = saturate(float3(
        dot(ycc, RowR.xyz) + RowR.w,
        dot(ycc, RowG.xyz) + RowG.w,
        dot(ycc, RowB.xyz) + RowB.w));

    float alpha = 1;
    if (Unpack != 0)
    {
        int2 at = Unpack == 1 ? int2(p.x + OutWidth, p.y) : int2(p.x, p.y + OutHeight);
        alpha = saturate((Luma.Load(int4(at, 0, 0)) - LumaScale.x) * LumaScale.y);
    }

    Output[p] = float4(rgb, alpha);
}
