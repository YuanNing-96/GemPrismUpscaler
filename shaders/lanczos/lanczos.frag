#version 450
layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;
layout(binding = 0) uniform sampler2D srcTex;

const float PI = 3.141592653589793;

// Lanczos-2 kernel: 4 lobes per axis, 4x4 taps.
float lanczos2(float x) {
    x = abs(x);
    if (x < 0.0001) return 1.0;
    if (x >= 2.0) return 0.0;
    float px = PI * x;
    return 2.0 * sin(px) * sin(px * 0.5) / (px * px);
}

void main() {
    ivec2 size = textureSize(srcTex, 0);
    vec2 texel = vUV * vec2(size) - 0.5;
    ivec2 base = ivec2(floor(texel));
    vec2 f = texel - vec2(base);

    vec4 sum = vec4(0.0);
    float wsum = 0.0;
    for (int y = -1; y <= 2; ++y) {
        float wy = lanczos2(float(y) - f.y);
        for (int x = -1; x <= 2; ++x) {
            float w = wy * lanczos2(float(x) - f.x);
            ivec2 p = clamp(base + ivec2(x, y), ivec2(0), size - 1);
            sum += texelFetch(srcTex, p, 0) * w;
            wsum += w;
        }
    }
    outColor = vec4(sum.rgb / wsum, 1.0);
}
