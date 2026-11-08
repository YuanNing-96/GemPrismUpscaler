#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <time.h>
#include <sys/mman.h>
#include <wayland-client.h>
#include <xkbcommon/xkbcommon.h>
#include "gemprism-vkeyboard-protocol.h"

static struct wl_seat *seat;
static struct zwp_virtual_keyboard_manager_v1 *manager;

static void registry_global(void *data, struct wl_registry *reg, uint32_t name,
                            const char *iface, uint32_t version) {
    (void)data;
    if (!strcmp(iface, "wl_seat"))
        seat = wl_registry_bind(reg, name, &wl_seat_interface, version < 7 ? version : 7);
    else if (!strcmp(iface, "zwp_virtual_keyboard_manager_v1"))
        manager = wl_registry_bind(reg, name, &zwp_virtual_keyboard_manager_v1_interface, 1);
}
static void registry_global_remove(void *data, struct wl_registry *reg, uint32_t name) {
    (void)data; (void)reg; (void)name;
}
static const struct wl_registry_listener registry_listener = {
    .global = registry_global,
    .global_remove = registry_global_remove,
};

static uint32_t now_ms(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint32_t)((ts.tv_sec * 1000ull + ts.tv_nsec / 1000000ull) & 0xffffffffu);
}

int main(void) {
    setvbuf(stdout, NULL, _IOLBF, 0);
    struct wl_display *display = wl_display_connect(NULL);
    if (!display) { fprintf(stderr, "gemprism-vkeyboard: no Wayland display\n"); return 1; }
    struct wl_registry *reg = wl_display_get_registry(display);
    wl_registry_add_listener(reg, &registry_listener, NULL);
    wl_display_roundtrip(display);
    if (!seat || !manager) { fprintf(stderr, "gemprism-vkeyboard: missing seat/manager\n"); return 1; }

    struct zwp_virtual_keyboard_v1 *kb =
        zwp_virtual_keyboard_manager_v1_create_virtual_keyboard(manager, seat);
    struct xkb_context *ctx = xkb_context_new(XKB_CONTEXT_NO_FLAGS);
    struct xkb_keymap *keymap = xkb_keymap_new_from_names(ctx, NULL, XKB_KEYMAP_COMPILE_NO_FLAGS);
    char *str = xkb_keymap_get_as_string(keymap, XKB_KEYMAP_FORMAT_TEXT_V1);
    size_t len = strlen(str) + 1;
    int fd = memfd_create("gemprism-vkeymap", 0);
    if (fd < 0 || ftruncate(fd, len) < 0) { perror("gemprism-vkeyboard: memfd"); return 1; }
    if (write(fd, str, len) != (ssize_t)len) { perror("gemprism-vkeyboard: keymap"); return 1; }
    lseek(fd, 0, SEEK_SET);
    zwp_virtual_keyboard_v1_keymap(kb, WL_KEYBOARD_KEYMAP_FORMAT_XKB_V1, fd, len);
    wl_display_roundtrip(display);
    close(fd);

    printf("READY\n");
    char line[256];
    while (fgets(line, sizeof(line), stdin)) {
        char cmd = line[0];
        if (cmd == 'K') {
            unsigned key, state;
            if (sscanf(line + 1, "%u %u", &key, &state) == 2) {
                zwp_virtual_keyboard_v1_key(kb, now_ms(), key, state);
                wl_display_flush(display);
            }
        } else if (cmd == 'M') {
            unsigned dep, lat, lock, group;
            if (sscanf(line + 1, "%u %u %u %u", &dep, &lat, &lock, &group) == 4) {
                zwp_virtual_keyboard_v1_modifiers(kb, dep, lat, lock, group);
                wl_display_flush(display);
            }
        } else if (cmd == 'Q') {
            break;
        }
    }

    zwp_virtual_keyboard_v1_destroy(kb);
    wl_display_roundtrip(display);
    xkb_keymap_unref(keymap);
    xkb_context_unref(ctx);
    wl_display_disconnect(display);
    return 0;
}
