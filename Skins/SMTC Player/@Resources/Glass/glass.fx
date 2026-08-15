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

    // Толстая линза: к краям карточки преломление усиливается, как у настоящего
    // стекла с фаской.
    // Знак минус: линза преломляет к центру, как настоящее стекло. Смещение
    // наружу уводило бы выборку за край снимка и красило периметр каймой.
    float2 c = uv - 0.5;
    float edge = saturate(dot(c, c) * 3.4);
    float2 lens = -c * edge * Amount * 2.2;

    float2 off = n * Amount * 1.8 + lens;

    // Каналы расходятся по-разному — так ведёт себя настоящее стекло. Больше
    // 0.2 от Amount дисперсия начинает красить однородный фон.
    float2 disp = n * Amount * 0.18;

    float4 col;
    col.r = tex2D(src, saturate(uv + off + disp)).r;
    col.g = tex2D(src, saturate(uv + off)).g;
    col.b = tex2D(src, saturate(uv + off - disp)).b;
    col.a = tex2D(src, saturate(uv + off)).a;

    // Блика здесь нет намеренно: он считался по разнице соседних сэмплов шума,
    // а билинейная интерполяция делает её ступенчатой — резкая степенная
    // функция превращала ступеньки в рябь по всему стеклу.
    return col;
}
