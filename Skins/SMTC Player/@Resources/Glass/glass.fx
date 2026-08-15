// Стекло: преломление фона по шумовому полю, краевая линза, дисперсия и блик.
// Вешается ТОЛЬКО на слой фона — под шейдером текст стал бы нечитаем.
// Сборка: fxc /T ps_3_0 /E main /Fo glass.ps glass.fx  (результат коммитится).
sampler2D src   : register(s0);
sampler2D noise : register(s1);

float Amount : register(c0);   // сила преломления, доли текстурных координат
float Phase  : register(c1);   // медленный дрейф — «жидкость» дышит

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 nuv = uv * 0.7 + float2(Phase * 0.017, Phase * 0.011);

    float2 n = tex2D(noise, nuv).rg - 0.5;

    // Градиент шума считаем вручную: он же служит псевдонормалью стекла,
    // по которой дальше кладётся блик.
    const float e = 0.004;
    float n0 = tex2D(noise, nuv).r;
    float nx = tex2D(noise, nuv + float2(e, 0)).r;
    float ny = tex2D(noise, nuv + float2(0, e)).r;
    float2 grad = float2(nx - n0, ny - n0);

    // Толстая линза: к краям карточки преломление усиливается, как у настоящего
    // стекла с фаской.
    float2 c = uv - 0.5;
    float edge = saturate(dot(c, c) * 3.4);
    float2 lens = c * edge * Amount * 2.2;

    float2 off = n * Amount * 2.6 + lens + grad * Amount * 6.0;

    // Каналы расходятся по-разному — так ведёт себя настоящее стекло.
    float2 disp = n * Amount * 0.55;

    float4 col;
    col.r = tex2D(src, saturate(uv + off + disp)).r;
    col.g = tex2D(src, saturate(uv + off)).g;
    col.b = tex2D(src, saturate(uv + off - disp)).b;
    col.a = tex2D(src, saturate(uv + off)).a;

    // Блик по «поверхности»: чем круче наклон, тем ярче отсвет.
    float3 normal = normalize(float3(grad * 90.0, 1.0));
    float3 light = normalize(float3(-0.55, -0.75, 0.62));
    float spec = pow(saturate(dot(normal, light)), 14.0);
    col.rgb += spec * 0.42;

    return col;
}
