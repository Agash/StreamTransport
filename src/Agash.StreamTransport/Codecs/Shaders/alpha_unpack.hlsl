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
    // Colour from the left half (x in [0,0.5)).
    float2 cuv = float2(i.uv.x * 0.5, i.uv.y);
    float  y = YPlane.Sample(Samp, cuv);
    float2 c = UVPlane.Sample(Samp, cuv);
    float Y = (y - 0.0625) * 1.164;
    float U = c.x - 0.5;
    float V = c.y - 0.5;
    float r = saturate(Y + 1.793 * V);
    float g = saturate(Y - 0.213 * U - 0.533 * V);
    float b = saturate(Y + 2.112 * U);

    // Alpha from the right-half luma (x in [0.5,1)), expanded from 16..235 limited range.
    float ya = YPlane.Sample(Samp, float2(0.5 + i.uv.x * 0.5, i.uv.y));
    float a = saturate((ya - 0.0625) * 1.164);
    return float4(r, g, b, a);
}
