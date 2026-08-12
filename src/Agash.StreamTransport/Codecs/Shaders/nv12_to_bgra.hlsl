Texture2D<float>  YPlane  : register(t0);
Texture2D<float2> UVPlane : register(t1);
SamplerState      Samp    : register(s0);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    o.uv  = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    float  y = YPlane.Sample(Samp, i.uv);
    float2 c = UVPlane.Sample(Samp, i.uv);
    float Y = (y - 0.0625) * 1.164;
    float U = c.x - 0.5;
    float V = c.y - 0.5;
    // BT.709, limited range -> full-range RGB.
    float r = saturate(Y + 1.793 * V);
    float g = saturate(Y - 0.213 * U - 0.533 * V);
    float b = saturate(Y + 2.112 * U);
    return float4(r, g, b, 1);
}
