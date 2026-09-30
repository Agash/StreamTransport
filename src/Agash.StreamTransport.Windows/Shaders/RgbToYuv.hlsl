// RGB to 4:2:0 Y'CbCr, written as a luma plane and an interleaved chroma plane. Each thread makes one
// chroma sample and the 2x2 luma block it covers. With Pack set, the output is twice the colour width:
// the left half is the colour, the right half the source alpha as grey (a, a, a), which the same matrix
// turns into luma with neutral chroma. That is the side-by-side alpha layout every StreamTransport
// platform writes and reads.

cbuffer Parameters : register(b0)
{
    uint OutWidth;      // luma width written
    uint OutHeight;     // luma height written
    uint ColourWidth;   // width of the colour picture in the output
    uint Pack;          // 1: the right half carries alpha
    float4 RowY;        // Y' = dot(rgb, RowY.xyz) + RowY.w
    float4 RowCb;
    float4 RowCr;
};

Texture2DArray<float4> Source : register(t0);
SamplerState Linear : register(s0);
RWTexture2D<float> Luma : register(u0);
RWTexture2D<float2> Chroma : register(u1);

float3 Colour(uint2 p)
{
    bool alpha = Pack != 0 && p.x >= ColourWidth;
    uint x = alpha ? p.x - ColourWidth : p.x;
    float2 uv = (float2(x, p.y) + 0.5) / float2(ColourWidth, OutHeight);
    float4 s = Source.SampleLevel(Linear, float3(uv, 0), 0);
    return alpha ? s.aaa : s.rgb;
}

[numthreads(8, 8, 1)]
void Main(uint3 id : SV_DispatchThreadID)
{
    uint2 c = id.xy;
    uint2 l = c * 2;
    if (l.x >= OutWidth || l.y >= OutHeight)
    {
        return;
    }

    uint2 right = uint2(min(l.x + 1, OutWidth - 1), l.y);
    uint2 below = uint2(l.x, min(l.y + 1, OutHeight - 1));
    uint2 diagonal = uint2(right.x, below.y);
    float3 s00 = Colour(l);
    float3 s10 = Colour(right);
    float3 s01 = Colour(below);
    float3 s11 = Colour(diagonal);

    Luma[l] = dot(s00, RowY.xyz) + RowY.w;
    Luma[right] = dot(s10, RowY.xyz) + RowY.w;
    Luma[below] = dot(s01, RowY.xyz) + RowY.w;
    Luma[diagonal] = dot(s11, RowY.xyz) + RowY.w;

    float3 average = (s00 + s10 + s01 + s11) * 0.25;
    Chroma[c] = float2(dot(average, RowCb.xyz) + RowCb.w, dot(average, RowCr.xyz) + RowCr.w);
}
