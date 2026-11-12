# v1.0 发布清单

## 1. 产物结构

目标：解压即用，扁平布局，双击 `GemPrismUpscaler` 启动。不做安装器、`.desktop`、Flatpak、AUR。

```text
GemPrismUpscaler-v1.0.0-linux-x64/
  GemPrismUpscaler
  gemprism.png
  LICENSE
  NOTICE
  README.md
  docs/
  shaders/
  runtime/sway/
    sway
    swaymsg
    libwlroots-0.20.so
    xdg-decoration-v2-shim.so
    LICENSE / LICENSE.wlroots
  runtime/gemprism-vkeyboard
  <dotnet publish 其余文件>
```

要求：

- [x] 文档迁移到可见 `docs/`
- [x] 用户文档、示例、链接检查
- [x] `dotnet publish -c Release -r linux-x64 --self-contained` 成功
- [x] 带固定版本 sway/swaymsg/wlroots/shim/vkeyboard
- [x] 带内置着色器与示例（或确认已嵌入/无需外部文件）
- [x] `LICENSE`、`NOTICE`、`README.md`
- [x] 主程序可执行权限；压缩包保留权限
- [x] `GemPrismUpscaler-v1.0.0-linux-x64.tar.gz` / `.zip` / `.sha256`
- [x] 包内不含开发资料、日志、`bin/obj`、测试产物、开发机路径

## 2. 干净环境验收

在新建用户目录/容器中：

1. [x] 解压到空目录。
2. [x] `./GemPrismUpscaler --version` → `1.0.0`。
3. [x] `./GemPrismUpscaler --doctor` → 无 `[FAIL]`。
4. [x] `./GemPrismUpscaler` → 三页签 GUI 正常。
5. [x] `--config-check` 使用 `docs/examples/config.toml` → exit 0。
6. [x] 嵌套启动冒烟：命令、画面、输入、暂停/继续、停止。
7. [x] 关闭窗口无残留 `sway`/XWayland/宿主进程。

## 3. 版本与发布

- [x] `Directory.Build.props` 版本改为 `1.0.0`
- [x] `--version` 输出 `1.0.0`
- [x] 最终 build 0 warning / 0 error
- [x] 最终 test 150/150
- [x] `v1.0.0` annotated tag
- [x] 发布说明/已知问题（`docs/release-notes.md`、`docs/known-issues.md`）
- [x] 验收清单交给用户；验收结果不阻塞发布

## 4. 明确不做

- Flatpak / AUR / install.sh / `.desktop`
- 基准套件、i18n、完整回归矩阵
- FPS/raw input、HDR 捕获、DMA-BUF 零拷贝、跨屏拼接