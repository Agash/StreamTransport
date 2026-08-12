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

float4 PSMain(VSOut i) : SV_Target
{
    // Output is 2W wide: [0,0.5) samples colour, [0.5,1) samples the alpha as greyscale.
    if (i.uv.x < 0.5)
    {
        float4 c = Src.Sample(Samp, float2(i.uv.x * 2.0, i.uv.y));
        return float4(c.rgb, 1);
    }

    float4 c = Src.Sample(Samp, float2((i.uv.x - 0.5) * 2.0, i.uv.y));
    return float4(c.a, c.a, c.a, 1);
}
