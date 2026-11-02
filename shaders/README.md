# 着色器

## Anime4K（M0b）

- `anime4k/Anime4K_Upscale_CNN_x2_S.glsl`
  - 来自 [Anime4K](https://github.com/bloc97/Anime4K) 的 `glsl/Upscale/Anime4K_Upscale_CNN_x2_S.glsl`（master，2026-11-02 获取）。
  - 许可证：MIT，见 `anime4k/LICENSE`。原文件头部的 MIT 声明保留。
  - 该文件是 **mpv user-shader** 格式，不能直接交给 glslang 编译。

`tools/build_shaders.py` 会：

1. 解析 `//!DESC` / `//!BIND` / `//!SAVE` / `//!WIDTH` 等指令；
2. 把 5 个 pass 展开成独立 Vulkan compute shader（`generated/anime4k_conv*.comp`、`anime4k_d2s.comp`）；
3. 用 `glslangValidator -V` 编译为 SPIR-V，并用 `spirv-val` 校验；
4. 把 `.spv` 复制到 `src/GemPrismUpscaler.Core/Shaders/`，作为嵌入资源随程序分发。

## Lanczos

- `lanczos/lanczos.vert`、`lanczos.frag`：项目自写的全屏三角形 + Lanczos-2（4×4 taps）收尾 pass。

## 重新生成

```bash
python3 tools/build_shaders.py
```

需要 `glslangValidator` 和 `spirv-val`（Arch: `glslang`、`spirv-tools`）。运行时不依赖这些工具，仓库中提交的 `.spv` 即最终产物。

不要手工编辑 `generated/` 和 `src/GemPrismUpscaler.Core/Shaders/*.spv`；改源着色器后重新运行脚本。
