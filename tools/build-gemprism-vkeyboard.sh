#!/bin/sh
# Build the M2b virtual-keyboard helper. Requires wayland-scanner, libwayland
# client headers and libxkbcommon development files.
set -eu
DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$DIR"
wayland-scanner client-header virtual-keyboard-unstable-v1.xml gemprism-vkeyboard-protocol.h
wayland-scanner private-code virtual-keyboard-unstable-v1.xml gemprism-vkeyboard-protocol.c
cc -O2 -o gemprism-vkeyboard gemprism-vkeyboard.c gemprism-vkeyboard-protocol.c \
    $(pkg-config --cflags --libs wayland-client xkbcommon) -lm
