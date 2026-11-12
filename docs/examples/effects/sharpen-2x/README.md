# sharpen-2x（示例自定义 effect）

这是 `docs/dev/effects.md` 的配套样例：2× 双线性放大 + unsharp mask 锐化，最后交给内置 Lanczos-2 收尾。

## 使用方法

```bash
mkdir -p ~/.config/GemPrismUpscaler/effects
cp -r docs/examples/effects/sharpen-2x ~/.config/GemPrismUpscaler/effects/
```

重启应用，然后在“超分”页的“超分算法”里选择“锐化 2×（示例）”。v1.0 不展示效果管理页，修改 effect 文件后需要重启。

## 文件

- `effect.toml`：pass 图与效果元数据。
- `sharpen.comp`：GLSL 源码，仅供参考。
- `sharpen.comp.spv`：预编译 SPIR-V，运行时只读取这个文件。

重新编译（开发时）：

```bash
glslangValidator -S comp -V --target-env vulkan1.2 \
  -o sharpen.comp.spv sharpen.comp
spirv-val sharpen.comp.spv
```
