# 自定义 effect 说明（ABI v1）

本文是 GemPrismUpscaler 自定义 effect 的**完整约定**。目标：其他人或 agent 只读这一篇，就能写出可被程序加载、运行、通过校验的 effect，不需要改 C# 代码，也不需要重新编译应用。

- 适用版本：v0.3.0（M1c）
- ABI 版本：**1**
- 运行时不需要 `glslangValidator`；`.spv` 是预编译产物
- 自定义 effect 只支持 **Vulkan compute pass**；最终统一由内置 Lanczos-2 收尾

---

## 1. 目录结构

用户 effect 放在配置目录的 `effects/` 下：

```text
~/.config/GemPrismUpscaler/
  config.toml
  effects/
    sharpen-2x/
      effect.toml
      sharpen.comp.spv
      sharpen.comp        # 可选，源码，仅供人阅读
```

加载器会扫描：

- `effects/*.toml`
- `effects/*/*.toml`（一层子目录）

`shader` 路径相对于 **TOML 所在目录**。同一个 effect 的 TOML 和 `.spv` 可以放在子目录里，整个目录复制即可。

`GEMPRISM_CONFIG` 可以改变配置目录；effects 目录始终是 `config.toml` 同级的 `effects/`。

---

## 2. ABI 版本与向后兼容

`abi` 是整数，当前只支持 `1`。

规则：

1. **同一 `abi` 内只做加法**：新增可选字段、放宽校验、增加内置能力可以；不改变已有字段含义、不改变绑定顺序、不改变 push constant 布局。
2. 任何会改变 shader 绑定、push constant 布局、坐标约定、纹理格式的改动，必须提升 `abi`。
3. 程序遇到 `abi > 1` 的 effect 会**跳过并记录日志**，不会崩溃，也不会尝试猜测。
4. 程序遇到 `abi = 1` 的未知字段会忽略；未知的 `[[pass]]` 可选字段同理。
5. `id` 是稳定标识。显示名 `name` 可以改，`id` 不要随便改，否则 Profile 和 `--effect` 引用会失效。

---

## 3. TOML 字段

顶层：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `abi` | int | 是 | 固定 `1` |
| `id` | string | 是 | 唯一 id；不能和内置 `bilinear`、`anime4k-s/m/l/vl` 冲突 |
| `name` | string | 否 | 界面显示名；缺省用 `id` |
| `radius` | int | 否 | 源图像素为单位的邻域半径；脏矩形 halo 用。默认 0 |
| `requires_full_frame` | bool | 否 | true 时该 effect 不使用脏矩形，每帧整帧 |
| `output_scale` | int | 否 | 最终输出倍率 1–4；缺省取最后一个 pass 的 `output_scale` |
| `[[texture]]` | table array | 否 | 显式声明中间纹理；通常可省略，由 pass 输出自动创建 |
| `[[pass]]` | table array | 是 | 至少一个 compute pass |

`[[texture]]`：

| 字段 | 类型 | 说明 |
|---|---|---|
| `name` | string | 纹理名，不能是 `MAIN` |
| `scale` | int | 1–4，相对源图倍率 |
| `external` | bool | 只允许 `MAIN` 为 true；其他纹理必须 false |

`[[pass]]`：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `name` | string | 否 | 调试用名字 |
| `shader` | string | 是 | 相对 TOML 目录的 `.spv` 路径 |
| `inputs` | string array | 是 | 输入纹理名，顺序就是 descriptor binding 顺序 |
| `output` | string | 是 | 输出纹理名，不能是 `MAIN`；同名只能写一次 |
| `output_scale` | int | 否 | 1–4，默认 1 |
| `workgroup` | int array | 否 | **ABI v1 固定 8×8×1**；该字段保留，当前不生效 |

约束：

- `inputs` 只能引用 `MAIN` 或**之前 pass 已经输出**的纹理。
- `output` 不能与之前 pass 的输出重名。
- 如果提前用 `[[texture]]` 声明了某张纹理，则 pass 的 `output_scale` 必须与声明一致。
- 最后一个 pass 的 `output` 就是交给 Lanczos 的最终图。
- 纹理总数上限 **19**（含 `MAIN`）；单个 pass 输入上限 **16**。
- push constant 总字节数上限 **256 B**；如果设备 `maxPushConstantsSize` 更小，程序会拒绝该 effect。

---

## 4. Shader 约定

### 4.1 基本形式

- 一个 pass 一个 compute shader，入口 `main`。
- `layout(local_size_x = 8, local_size_y = 8) in;`
- 输出图片是 `rgba16f` storage image。
- 输入是普通 `sampler2D`，线性过滤，ClampToEdge，无 mipmap。
- 源图 `MAIN` 是 BGRA8，程序已上传为可采样 image；shader 侧按普通 `vec4` 采样即可。

### 4.2 descriptor 绑定

对每个 pass，设 `N = inputs.Count`：

| binding | 内容 |
|---|---|
| `0 .. N-1` | `layout(binding = i) uniform sampler2D <inputs[i]>_sampler;` |
| `N` | `layout(rgba16f, binding = N) uniform image2D outImage;` |

绑定顺序**严格等于** TOML 里 `inputs` 的顺序。

### 4.3 push constant

固定布局，不能增删字段、不能改顺序：

```glsl
layout(push_constant) uniform PC {
    vec2 outSize;        // 该 pass 的完整输出尺寸（像素）
    vec2 regionOffset;   // 本次 dispatch 的左上角（输出像素）
    vec2 regionSize;     // 本次 dispatch 的尺寸（输出像素）
    vec2 texSize[N];     // 所有纹理的完整尺寸，顺序见下
} pc;
```

- `N` 是**该 effect 的最终纹理总数**（含 `MAIN` 和所有 pass 输出），不是当前 pass 的输入数。
- 字节数 = `24 + 8*N`。例如 2 张纹理 = 40 B；19 张 = 176 B。
- `texSize` 的下标顺序 = 纹理创建顺序：`0` 是 `MAIN`，之后按 pass 输出顺序 / `[[texture]]` 顺序。
  - **重要**：`texSize[N]` 的数组长度必须与最终纹理总数一致；否则 pipeline layout 与 shader 不匹配。先确定 pass 图，数清纹理总数再写。
- `outSize` 是完整输出；`regionOffset/regionSize` 是本次处理的子区域。
- `texSize[i]` 是完整纹理尺寸；用 `1.0 / texSize[i]` 作为像素步长。

### 4.4 区域与坐标

```glsl
void main() {
    ivec2 local = ivec2(gl_GlobalInvocationID.xy);
    ivec2 regionSize = ivec2(pc.regionSize);
    if (local.x >= regionSize.x || local.y >= regionSize.y) return;

    ivec2 p = ivec2(pc.regionOffset) + local;
    vec2 uv = (vec2(p) + vec2(0.5)) / pc.outSize;
    vec2 srcUv = (vec2(p) + vec2(0.5)) / (2.0 * pc.texSize[0]); // 2× 示例
    ...
    imageStore(outImage, p, result);
}
```

规则：

- 只允许写 `[regionOffset, regionOffset+regionSize)` 内的像素。
- 读取可以在任意位置；边界由 sampler 的 ClampToEdge 处理。
- 归一化坐标与整帧/局部无关：`uv = (p + 0.5) / outSize` 在两种情况下一致。
- 如果某个 pass 需要读取当前区域外的邻居，把 `radius` 设成足够大，程序会把脏矩形扩展后再 dispatch；否则局部更新会出现接缝。
- 如果算法无法按区域安全执行（例如需要全局统计、跨区域模糊），设 `requires_full_frame = true`。

### 4.5 输出与收尾

- 最后一个 pass 的输出纹理交给内置 **Lanczos-2** 重采样到屏幕内容矩形。
- 前面的 CNN/滤镜只需要输出“2×（或 1–4×）的中间图”，不需要自己处理黑边或最终尺寸。
- 输出宽高 = 源宽高 × 最后一个 pass 的 `output_scale`。

---

## 5. 完整样例：锐化 2×

见 `examples/effects/sharpen-2x/`。

`effect.toml`：

```toml
abi = 1
id = "sharpen-2x"
name = "锐化 2×（示例）"
radius = 2
requires_full_frame = false

[[pass]]
name = "sharpen"
shader = "sharpen.comp.spv"
inputs = ["MAIN"]
output = "upscaled2x"
output_scale = 2
```

`sharpen.comp` 关键部分：

```glsl
#version 450
layout(local_size_x = 8, local_size_y = 8) in;

layout(binding = 0) uniform sampler2D MAIN_sampler;
layout(rgba16f, binding = 1) uniform image2D outImage;

layout(push_constant) uniform PC {
    vec2 outSize;
    vec2 regionOffset;
    vec2 regionSize;
    vec2 texSize[2];   // MAIN + upscaled2x
} pc;

void main() {
    ivec2 local = ivec2(gl_GlobalInvocationID.xy);
    ivec2 regionSize = ivec2(pc.regionSize);
    if (local.x >= regionSize.x || local.y >= regionSize.y) return;

    ivec2 p = ivec2(pc.regionOffset) + local;
    vec2 srcSize = pc.texSize[0];
    vec2 uv = (vec2(p) + vec2(0.5)) / (2.0 * srcSize);
    vec2 px = 1.0 / srcSize;

    vec4 c = texture(MAIN_sampler, uv);
    vec4 blur = (
        texture(MAIN_sampler, uv + vec2(px.x, 0.0)) +
        texture(MAIN_sampler, uv - vec2(px.x, 0.0)) +
        texture(MAIN_sampler, uv + vec2(0.0, px.y)) +
        texture(MAIN_sampler, uv - vec2(0.0, px.y))
    ) * 0.25;

    vec3 sharp = c.rgb + (c.rgb - blur.rgb) * 0.7;
    imageStore(outImage, p, vec4(clamp(sharp, 0.0, 1.0), c.a));
}
```

编译（只在开发时）：

```bash
glslangValidator -S comp -V --target-env vulkan1.2 \
  -o sharpen.comp.spv sharpen.comp
spirv-val sharpen.comp.spv
```

安装：

```bash
mkdir -p ~/.config/GemPrismUpscaler/effects
cp -r examples/effects/sharpen-2x ~/.config/GemPrismUpscaler/effects/
```

重启应用，或到“效果”Tab 点“重新加载”，然后在“会话/设置”的超分算法里选择“锐化 2×（示例）”。

命令行：

```bash
./GemPrismUpscaler --window <id> --effect sharpen-2x
```

---

## 6. 校验与错误

加载时程序会检查：

- `abi` 是否为 1；
- `id` 是否为空、是否与内置/已有 effect 冲突；
- 至少一个 pass；
- 每个 pass 的 `shader` 文件存在，且前 4 字节是 SPIR-V magic `0x07230203`；
- `inputs` 是否引用已生成纹理；
- `output` 是否重复或为 `MAIN`；
- 纹理数 ≤ 19、单 pass 输入 ≤ 16、push constant ≤ 256 B；
- 最后一个 pass 的输出纹理存在。

失败时：

- 日志出现 `custom effect skipped (...)` 或 `custom effect 'id' skipped: ...`；
- 效果不会出现在列表里；
- 应用继续正常运行，不会因为一个坏 effect 启动失败。

运行期如果 shader 与 pipeline layout 不匹配、SPIR-V 校验失败、显存不足等，会话启动会失败并显示功能级错误；该 effect 需要修正。

---

## 7. 限制与建议

- ABI v1 只支持 compute pass；不支持自定义 graphics 最终 pass。
- 固定 8×8 workgroup；`workgroup` 字段保留。
- 纹理格式固定 `rgba16f`；源 `MAIN` 为 BGRA8。
- 纹理总数 19；单 pass 输入 16；push constant 256 B（设备较老时可能更低，程序会检查）。
- 推荐先写一个单 pass 的简单滤镜（如本样例），确认 ABI 后再加多 pass。
- 多 pass 时，`texSize[N]` 的 `N` 是最终总数，先数清再编译。
- 脏矩形是默认路径；如果 effect 需要整帧，务必设 `requires_full_frame = true`，否则可能出现接缝。
- 只安装你信任的 `.spv`：它在 GPU 上执行，和普通程序一样有安全边界。
- 性能：中间图越大、pass 越多、采样越多，GPU/显存开销越高；4K 输出下尤其明显。

---

## 8. 版本记录

| ABI | 版本 | 说明 |
|---|---|---|
| 1 | v0.3.0 | 首个公开约定：compute pass、固定 push constant、19 纹理、Lanczos-2 收尾 |
