/*
 * xdg-decoration-v2-shim
 *
 * Wine's Wayland driver (as shipped by GE-Proton / Proton) binds
 * zxdg_decoration_manager_v1 at version 2 and only negotiates server-side
 * decorations when the compositor advertises version > 1. sway 1.12 with
 * wlroots 0.20 advertises version 1, so Wine never binds the decoration
 * manager and falls back to drawing its own Windows-style CSD titlebar.
 *
 * This shim interposes wl_global_create() and bumps only that one global's
 * advertised version from 1 to 2. wlroots' implementation already exposes
 * interface version 2; the only protocol difference in v2 is that a client may
 * create a decoration object after the toplevel has a buffer attached, which
 * Wine does not rely on (it creates the decoration object at toplevel
 * creation). Everything else is passed through unchanged.
 *
 * If the compositor already advertises version 2 (newer wlroots/sway), this
 * shim is a no-op. If LD_PRELOAD cannot be used, sway still starts normally
 * and simply advertises version 1 again.
 */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdlib.h>
#include <string.h>
#include <wayland-server-core.h>

typedef struct wl_global *(*wl_global_create_fn)(struct wl_display *display,
                                                 const struct wl_interface *interface,
                                                 int version, void *data,
                                                 wl_global_bind_func_t bind);

static wl_global_create_fn real_wl_global_create;
static int hide_syncobj;

__attribute__((constructor)) static void init_hide_syncobj(void)
{
    const char *value = getenv("GEMPRISM_HIDE_SYNCOBJ");
    hide_syncobj = value && value[0] && value[0] != '0' &&
                   strcmp(value, "off") != 0 && strcmp(value, "false") != 0;
}

struct wl_global *wl_global_create(struct wl_display *display,
                                   const struct wl_interface *interface,
                                   int version, void *data,
                                   wl_global_bind_func_t bind)
{
    if (!real_wl_global_create)
        real_wl_global_create = (wl_global_create_fn)dlsym(RTLD_NEXT, "wl_global_create");
    if (!real_wl_global_create)
        return NULL;

    if (interface && interface->name && version < 2 &&
        strcmp(interface->name, "zxdg_decoration_manager_v1") == 0)
        version = 2;

    /* Diagnostic switch: hide explicit-sync so clients fall back to implicit
     * fences. Used to isolate compositor-side syncobj handling. */
    if (hide_syncobj && interface && interface->name &&
        strcmp(interface->name, "wp_linux_drm_syncobj_manager_v1") == 0)
        return NULL;

    return real_wl_global_create(display, interface, version, data, bind);
}