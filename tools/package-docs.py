#!/usr/bin/env python3
"""Copy the user-facing documentation subset into a release tree."""
from __future__ import annotations

import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

DOCS = [
    "install.md",
    "quickstart.md",
    "usage.md",
    "cli.md",
    "configuration.md",
    "compatibility.md",
    "troubleshooting.md",
    "known-issues.md",
]
EXAMPLES = ["config.toml", "visual-novel.toml", "nested-lutris.sh"]

INDEX = """# 使用说明

v1.0 只明确支持 **KDE Plasma 6 Wayland + XWayland**。

| 文档 | 内容 |
|---|---|
| [安装](install.md) | 解压、权限、首次运行、卸载 |
| [快速开始](quickstart.md) | 三分钟跑通一次超分 |
| [使用说明](usage.md) | 界面、启动方式、暂停/停止、托盘、热键 |
| [命令行](cli.md) | 参数、退出码、脚本示例 |
| [配置](configuration.md) | `config.toml`、环境变量、Profile 现状 |
| [兼容性](compatibility.md) | 支持矩阵、已验证环境、性能参考 |
| [常见问题](troubleshooting.md) | 黑屏、启动失败、输入异常、诊断 |
| [已知问题](known-issues.md) | v1.0 不解决的问题与规避 |
| [示例](examples/) | 配置与 Lutris 嵌套脚本 |

发布包只包含使用说明；架构、历史资料和发布清单在源码仓库的 `docs/dev/` 中。
"""


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: package-docs.py <release-root>", file=sys.stderr)
        return 2
    stage = Path(sys.argv[1]).resolve()
    docs = stage / "docs"
    examples = docs / "examples"
    if docs.exists():
        shutil.rmtree(docs)
    examples.mkdir(parents=True)
    (docs / "README.md").write_text(INDEX, encoding="utf-8")

    for name in DOCS:
        text = (ROOT / "docs" / name).read_text(encoding="utf-8")
        if name == "configuration.md":
            marker = "## 7. 自定义 effect（高级）"
            pos = text.find(marker)
            if pos >= 0:
                text = text[:pos].rstrip() + "\n"
        if name == "known-issues.md":
            text = text.replace(
                "需要完整背景可看[开发文档](dev/README.md)里的历史资料。",
                "需要完整背景可看源码仓库中的开发文档。")
            text = text.replace(
                "v1.0 之后可能处理的方向见[开发计划](dev/post-v1.0.md)。",
                "v1.0 之后可能处理的方向见源码仓库中的开发计划。")
        (docs / name).write_text(text, encoding="utf-8")

    for name in EXAMPLES:
        src = ROOT / "docs" / "examples" / name
        shutil.copy2(src, examples / name)
    (examples / "nested-lutris.sh").chmod(0o755)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())