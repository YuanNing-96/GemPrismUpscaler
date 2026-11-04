// M1b zero-copy feasibility probe: XCompositeNameWindowPixmap -> DRI3 buffer(s).
// Prints whether the window pixmap can be exported as a DMA-BUF fd, plus the
// modifier and the DRM device behind the fd.
#include <xcb/xcb.h>
#include <xcb/dri3.h>
#include <xcb/composite.h>
#include <xf86drm.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <unistd.h>
#include <sys/stat.h>

static void print_fd(const char *label, int fd) {
    struct stat st;
    printf("  %s fd=%d", label, fd);
    if (fstat(fd, &st) == 0) printf(" size=%lld", (long long)st.st_size);
    drmVersionPtr v = drmGetVersion(fd);
    if (v) {
        printf(" drm=%s", v->name ? v->name : "?");
        if (v->desc) printf(" desc=%s", v->desc);
        drmFreeVersion(v);
    } else {
        printf(" drm=unknown");
    }
    printf("\n");
}

int main(int argc, char **argv) {
    if (argc < 2) { fprintf(stderr, "usage: %s <window-id>\n", argv[0]); return 2; }
    uint32_t win = (uint32_t)strtoul(argv[1], NULL, 0);
    int screen = 0;
    xcb_connection_t *c = xcb_connect(NULL, &screen);
    if (xcb_connection_has_error(c)) { fprintf(stderr, "xcb connect failed\n"); return 1; }

    const xcb_query_extension_reply_t *dri3 = xcb_get_extension_data(c, &xcb_dri3_id);
    const xcb_query_extension_reply_t *comp = xcb_get_extension_data(c, &xcb_composite_id);
    printf("window=0x%x dri3_present=%d composite_present=%d\n", win, dri3 && dri3->present, comp && comp->present);
    if (!dri3 || !dri3->present || !comp || !comp->present) return 1;

    xcb_dri3_query_version_reply_t *q = xcb_dri3_query_version_reply(c,
        xcb_dri3_query_version(c, 1, 2), NULL);
    if (q) { printf("dri3 version=%u.%u\n", q->major_version, q->minor_version); free(q); }

    xcb_composite_query_version_reply_t *cq = xcb_composite_query_version_reply(c,
        xcb_composite_query_version(c, 0, 4), NULL);
    if (cq) { printf("composite version=%u.%u\n", cq->major_version, cq->minor_version); free(cq); }

    xcb_get_geometry_reply_t *geom = xcb_get_geometry_reply(c, xcb_get_geometry(c, win), NULL);
    if (!geom) { fprintf(stderr, "get_geometry failed\n"); return 1; }
    printf("window geom=%ux%u depth=%u\n", geom->width, geom->height, geom->depth);
    uint8_t depth = geom->depth;
    uint8_t bpp = depth <= 24 ? 32 : depth;
    free(geom);

    xcb_dri3_get_supported_modifiers_reply_t *mods = xcb_dri3_get_supported_modifiers_reply(c,
        xcb_dri3_get_supported_modifiers(c, win, depth, bpp), NULL);
    if (mods) {
        printf("supported modifiers: window=%d screen=%d\n",
            xcb_dri3_get_supported_modifiers_window_modifiers_length(mods),
            xcb_dri3_get_supported_modifiers_screen_modifiers_length(mods));
        free(mods);
    }

    xcb_pixmap_t pixmap = xcb_generate_id(c);
    xcb_generic_error_t *err = NULL;
    xcb_void_cookie_t name_cookie = xcb_composite_name_window_pixmap_checked(c, win, pixmap);
    err = xcb_request_check(c, name_cookie);
    if (err) {
        printf("NameWindowPixmap FAILED error=%u\n", err->error_code);
        free(err); return 1;
    }
    printf("NameWindowPixmap OK pixmap=0x%x\n", pixmap);

    xcb_dri3_buffers_from_pixmap_reply_t *breply =
        xcb_dri3_buffers_from_pixmap_reply(c, xcb_dri3_buffers_from_pixmap(c, pixmap), &err);
    if (breply) {
        printf("BuffersFromPixmap OK nfd=%u size=%ux%u depth=%u bpp=%u modifier=0x%llx\n",
            breply->nfd, breply->width, breply->height, breply->depth, breply->bpp,
            (unsigned long long)breply->modifier);
        int32_t *fds = xcb_dri3_buffers_from_pixmap_reply_fds(c, breply);
        for (unsigned i = 0; i < breply->nfd; i++) {
            char label[32]; snprintf(label, sizeof label, "buf%u", i);
            print_fd(label, fds[i]);
        }
        free(breply);
    } else {
        printf("BuffersFromPixmap FAILED error=%u\n", err ? err->error_code : 0);
        if (err) free(err);
    }

    err = NULL;
    xcb_dri3_buffer_from_pixmap_reply_t *legacy =
        xcb_dri3_buffer_from_pixmap_reply(c, xcb_dri3_buffer_from_pixmap(c, pixmap), &err);
    if (legacy) {
        printf("BufferFromPixmap OK nfd=%u size=%ux%u stride=%u depth=%u bpp=%u\n",
            legacy->nfd, legacy->width, legacy->height, legacy->stride,
            legacy->depth, legacy->bpp);
        int32_t *fds = xcb_dri3_buffer_from_pixmap_reply_fds(c, legacy);
        for (unsigned i = 0; i < legacy->nfd; i++) {
            char label[32]; snprintf(label, sizeof label, "legacy%u", i);
            print_fd(label, fds[i]);
        }
        free(legacy);
    } else {
        printf("BufferFromPixmap FAILED error=%u\n", err ? err->error_code : 0);
        if (err) free(err);
    }

    xcb_free_pixmap(c, pixmap);
    xcb_disconnect(c);
    return 0;
}
