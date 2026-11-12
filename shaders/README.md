# 着色器

## Anime4K（M1b/M1c）

- `anime4k/Anime4K_Upscale_CNN_x2_S.glsl`、`_M.glsl`、`_L.glsl`、`_VL.glsl`
  - 来自 [Anime4K](https://github.com/bloc97/Anime4K) 的 `glsl/Upscale/`（v3.2，2026-11-04 获取）。
  - 许可证：MIT，见 `anime4k/LICENSE`。原文件头部的 MIT 声明保留。
  - 文件是 **mpv user-shader** 格式，不能直接交给 glslang 编译。
  - S=5 pass / 6 张纹理；M=9 pass / 10 张纹理；L=10 pass / 11 张纹理（含并行分支）；**VL=18 pass / 19 张纹理 / push constant 176 B**（实验最高画质档）。

`tools/build_shaders.py` 会：

1. 解析 `//!DESC` / `//!BIND` / `//!SAVE` / `//!WIDTH` / `//!HEIGHT`；
2. 为每个 variant 展开成独立 Vulkan compute shader（`generated/anime4k_{s,m,l,vl}_pN.comp`）；
3. 用 `glslangValidator -V` 编译为 SPIR-V，并用 `spirv-val` 校验；
4. 生成 `generated/anime4k_manifest.json`，描述每个 variant 的 pass 图（输入/输出/尺寸/半径/push constant 大小）；
5. 把 `.spv` 和 manifest 复制到 `src/GemPrismUpscaler.Core/Shaders/`，作为嵌入资源随程序分发。

生成的 compute shader 使用统一的 push constant：

```glsl
vec2 outSize;        // 该 pass 的完整输出尺寸
vec2 regionOffset;   // 本次 dispatch 的左上角
vec2 regionSize;     // 本次 dispatch 的尺寸
vec2 texSize[N];     // 所有绑定纹理的完整尺寸（manifest 顺序）
```

`regionOffset` / `regionSize` 是脏矩形的入口：整帧渲染 dispatch 整个输出，局部更新 dispatch 扩展后的脏矩形；shader 仍用 `outSize` 计算归一化坐标。

VL 的 push constant 是 **176 B**（19 张纹理），超过 Vulkan 保证的 128 B；运行时查询设备的 `maxPushConstantsSize`，不足时 VL 不可用（S/M/L 仍为 72/104/112 B）。

## 自定义 effect

用户 effect 不在本目录编译；它们的 TOML + 预编译 `.spv` 放在配置目录的 `effects/` 下。ABI、绑定和 push constant 约定见 [docs/dev/effects.md](../docs/dev/effects.md)，样例见 `docs/examples/effects/sharpen-2x/`。

## Lanczos

- `lanczos/lanczos.vert`、`lanczos.frag`：项目自写的全屏三角形 + Lanczos-2（4×4 taps）收尾 pass。

## 重新生成

```bash
python3 tools/build_shaders.py
```

需要 `glslangValidator` 和 `spirv-val`（Arch: `glslang`、`spirv-tools`）。运行时不依赖这些工具，仓库中提交的 `.spv` 即最终产物。

不要手工编辑 `generated/` 和 `src/GemPrismUpscaler.Core/Shaders/*.spv`；改源着色器后重新运行脚本。