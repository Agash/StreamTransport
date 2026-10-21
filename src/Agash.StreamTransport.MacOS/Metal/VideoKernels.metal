#include <metal_stdlib>
using namespace metal;

// RGB to 4:2:0 Y'CbCr, written as a luma plane and an interleaved chroma plane. Each thread makes
// one chroma sample and the 2x2 luma block it covers. Chroma is left-sited, as H.264, H.265 and AV1
// assume when nothing says otherwise: each sample sits on its block's left column and between its
// two rows, so it is a [1 2 1] filter across the column and its neighbours, averaged over the two
// rows. With pack set, the output is twice the colour width: the left half is the colour, the right
// half the source alpha as grey (a, a, a), which the same matrix turns into luma with neutral
// chroma. That is the side-by-side alpha layout every StreamTransport platform writes and reads.
struct RgbToYuvParameters
{
    uint outWidth;     // luma width written
    uint outHeight;    // luma height written
    uint colourWidth;  // width of the colour picture in the output
    uint pack;         // 1: the right half carries alpha
    float4 rowY;       // Y' = dot(rgb, rowY.xyz) + rowY.w
    float4 rowCb;
    float4 rowCr;
};

static float3 colour_at(texture2d<float, access::sample> source, sampler linear,
                        constant RgbToYuvParameters& p, uint2 at)
{
    bool alpha = p.pack != 0 && at.x >= p.colourWidth;
    uint x = alpha ? at.x - p.colourWidth : at.x;
    float2 uv = (float2(x, at.y) + 0.5) / float2(p.colourWidth, p.outHeight);
    float4 s = source.sample(linear, uv, level(0));
    return alpha ? s.aaa : s.rgb;
}

kernel void rgb_to_yuv(texture2d<float, access::sample> source [[texture(0)]],
                       texture2d<float, access::write> luma [[texture(1)]],
                       texture2d<float, access::write> chroma [[texture(2)]],
                       constant RgbToYuvParameters& p [[buffer(0)]],
                       uint2 id [[thread_position_in_grid]])
{
    constexpr sampler linear(coord::normalized, address::clamp_to_edge, filter::linear);
    uint2 l = id * 2;
    if (l.x >= p.outWidth || l.y >= p.outHeight)
    {
        return;
    }

    uint2 right = uint2(min(l.x + 1, p.outWidth - 1), l.y);
    uint2 below = uint2(l.x, min(l.y + 1, p.outHeight - 1));
    uint2 diagonal = uint2(right.x, below.y);
    float3 s00 = colour_at(source, linear, p, l);
    float3 s10 = colour_at(source, linear, p, right);
    float3 s01 = colour_at(source, linear, p, below);
    float3 s11 = colour_at(source, linear, p, diagonal);

    luma.write(float4(dot(s00, p.rowY.xyz) + p.rowY.w), l);
    luma.write(float4(dot(s10, p.rowY.xyz) + p.rowY.w), right);
    luma.write(float4(dot(s01, p.rowY.xyz) + p.rowY.w), below);
    luma.write(float4(dot(s11, p.rowY.xyz) + p.rowY.w), diagonal);

    // The left neighbour stays inside the half it belongs to: the colour's first column, or the alpha's.
    uint start = p.pack != 0 && l.x >= p.colourWidth ? p.colourWidth : 0;
    uint left = max(l.x, start + 1) - 1;
    float3 sl0 = colour_at(source, linear, p, uint2(left, l.y));
    float3 sl1 = colour_at(source, linear, p, uint2(left, below.y));
    float3 average = (sl0 + sl1 + 2 * (s00 + s01) + s10 + s11) * 0.125;
    chroma.write(float4(dot(average, p.rowCb.xyz) + p.rowCb.w,
                        dot(average, p.rowCr.xyz) + p.rowCr.w, 0, 0), id);
}

// 4:2:0 Y'CbCr planes to RGB. With unpack set, the input is a side-by-side frame twice the output
// width: colour from the left half, and alpha from the right half's luma, normalised like the colour's.
// Chroma is left-sited (H.264, H.265 and AV1's default): a sample sits on every other column, between two
// rows. So an even column takes it as it is and an odd one the mean of its two neighbours, and each row
// weighs the chroma row it falls in 3:1 against the next nearest.
struct YuvToRgbParameters
{
    uint outWidth;
    uint outHeight;
    uint unpack;       // 1: alpha comes from the right half's luma
    uint padding;
    float4 lumaScale;  // x: offset subtracted from Y', y: scale after it, z: neutral chroma
    float4 rowR;       // R = dot(float3(Y, Cb, Cr), rowR.xyz) + rowR.w, on normalised Y, Cb, Cr
    float4 rowG;
    float4 rowB;
};

// The chroma for luma column x on chroma row r, interpolated across columns.
static float2 chroma_row(texture2d<float, access::read> chroma, uint x, uint r, uint last)
{
    uint c = x / 2;
    float2 here = chroma.read(uint2(c, r)).rg;
    return (x & 1) == 0 ? here : (here + chroma.read(uint2(min(c + 1, last), r)).rg) * 0.5;
}

kernel void yuv_to_rgb(texture2d<float, access::read> luma [[texture(0)]],
                       texture2d<float, access::read> chroma [[texture(1)]],
                       texture2d<float, access::write> output [[texture(2)]],
                       constant YuvToRgbParameters& p [[buffer(0)]],
                       uint2 id [[thread_position_in_grid]])
{
    if (id.x >= p.outWidth || id.y >= p.outHeight)
    {
        return;
    }

    float y = (luma.read(id).r - p.lumaScale.x) * p.lumaScale.y;
    uint lastColumn = (p.outWidth + 1) / 2 - 1;
    uint row = id.y / 2;
    uint other = (id.y & 1) == 0 ? max(row, 1u) - 1 : min(row + 1, (p.outHeight + 1) / 2 - 1);
    float2 cbcr = chroma_row(chroma, id.x, row, lastColumn) * 0.75
        + chroma_row(chroma, id.x, other, lastColumn) * 0.25 - p.lumaScale.z;
    float3 ycc = float3(y, cbcr);
    float3 rgb = saturate(float3(dot(ycc, p.rowR.xyz) + p.rowR.w,
                                 dot(ycc, p.rowG.xyz) + p.rowG.w,
                                 dot(ycc, p.rowB.xyz) + p.rowB.w));

    float alpha = 1;
    if (p.unpack != 0)
    {
        alpha = saturate((luma.read(uint2(id.x + p.outWidth, id.y)).r - p.lumaScale.x) * p.lumaScale.y);
    }

    output.write(float4(rgb, alpha), id);
}
