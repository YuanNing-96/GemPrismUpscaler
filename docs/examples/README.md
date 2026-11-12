# 示例

| 文件 | 用途 |
|---|---|
| [config.toml](config.toml) | 完整配置示例，含注释；可复制后按需修改。 |
| [visual-novel.toml](visual-novel.toml) | `[[profile]]` 片段示例（v1.0 不会自动套用，保留给后续版本）。 |
| [nested-lutris.sh](nested-lutris.sh) | 用 Lutris 启动嵌套会话的脚本模板。 |
| [effects/sharpen-2x/](effects/sharpen-2x/README.md) | 自定义 effect 样例（高级，GUI 不展示管理页）。 |

## 配置示例

不要直接覆盖正在使用的配置。建议先备份，再复制需要的字段：

```bash
cp ~/.config/GemPrismUpscaler/config.toml ~/.config/GemPrismUpscaler/config.toml.bak
cp docs/examples/config.toml ~/.config/GemPrismUpscaler/config.toml
./GemPrismUpscaler --config-check
```

`config.toml` 里的 `[nested] command` 需要换成你自己的游戏启动命令。

## 嵌套启动脚本

```bash
chmod +x docs/examples/nested-lutris.sh
./docs/examples/nested-lutris.sh HDMI-A-1 3
```

脚本只是模板：`GEMPRISM_APP` 指向主程序，第一个参数是显示器，第二个参数是 Lutris 游戏 id。使用前先完全退出 Lutris。

## 自定义 effect

```bash
mkdir -p ~/.config/GemPrismUpscaler/effects
cp -r docs/examples/effects/sharpen-2x ~/.config/GemPrismUpscaler/effects/
```

然后重启程序，在“超分算法”里选择“锐化 2×（示例）”。ABI 见[开发文档](../dev/effects.md)。