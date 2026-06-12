Texture2D<float4> Src  : register(t0);
SamplerState      Samp : register(s0);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    o.uv  = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

// BT.709 limited range (matches AlphaPacking's CPU matrix).
float PSY(VSOut i) : SV_Target
{
    float3 c = Src.Sample(Samp, i.uv).rgb;
    return (((47.0 * c.r) + (157.0 * c.g) + (16.0 * c.b)) / 256.0) + (16.0 / 255.0);
}

float2 PSUV(VSOut i) : SV_Target
{
    float3 c = Src.Sample(Samp, i.uv).rgb;
    float u = (((-26.0 * c.r) - (87.0 * c.g) + (112.0 * c.b)) / 256.0) + (128.0 / 255.0);
    float v = (((112.0 * c.r) - (102.0 * c.g) - (10.0 * c.b)) / 256.0) + (128.0 / 255.0);
    return float2(u, v);
}
