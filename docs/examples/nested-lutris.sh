#!/usr/bin/env bash
# 用 Lutris 启动一个嵌套超分会话的脚本模板。
#
# 用法：
#   ./nested-lutris.sh <显示器名称> <Lutris 游戏 id>
# 例：
#   ./nested-lutris.sh HDMI-A-1 3
#
# 使用前请完全退出 Lutris（包括托盘图标）。
# 显示器名称来自：./GemPrismUpscaler --list

set -euo pipefail

usage() {
    echo "usage: $0 <monitor> <lutris-game-id>" >&2
    echo "example: $0 HDMI-A-1 3" >&2
    exit 2
}

[[ $# -eq 2 ]] || usage

monitor=$1
game_id=$2
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
app=${GEMPRISM_APP:-"$script_dir/../../GemPrismUpscaler"}

if [[ ! -x "$app" ]]; then
    echo "GemPrismUpscaler not found or not executable: $app" >&2
    echo "set GEMPRISM_APP=/path/to/GemPrismUpscaler" >&2
    exit 1
fi

exec "$app" --nested --monitor "$monitor" -- \
    env LUTRIS_SKIP_INIT=1 lutris "lutris:rungameid/$game_id"