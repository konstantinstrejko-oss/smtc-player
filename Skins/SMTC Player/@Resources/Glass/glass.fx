// Стекло: смещение картинки по шумовому полю + лёгкая дисперсия по краям.
// Вешается ТОЛЬКО на слой размытой обложки — под ним текст стал бы нечитаем.
// Сборка: fxc /T ps_3_0 /E main /Fo glass.ps glass.fx  (результат коммитится).
sampler2D src   : register(s0);
sampler2D noise : register(s1);

float Amount : register(c0);   // сила смещения, доли текстурных координат
float Phase  : register(c1);   // медленный дрейф — «жидкость» дышит

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 nuv = uv * 0.7 + float2(Phase * 0.017, Phase * 0.011);
    float2 n = tex2D(noise, nuv).rg - 0.5;

    float2 d = uv + n * Amount;

    // Каналы расходятся чуть по-разному — так ведёт себя настоящее стекло.
    float2 disp = n * Amount * 0.22;
    float4 c;
    c.r = tex2D(src, d + disp).r;
    c.g = tex2D(src, d).g;
    c.b = tex2D(src, d - disp).b;
    c.a = tex2D(src, d).a;
    return c;
}
