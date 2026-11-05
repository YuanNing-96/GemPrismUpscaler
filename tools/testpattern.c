// GemPrismUpscaler M1b test pattern.
//
// Modes:
//   --static      draw once and stop (static-frame GPU check)
//   --dirty       draw once, then change a 64x64 block after --delay-ms
//   --animate     move a 64x64 block continuously (small dirty regions)
//   --repaint     redraw the same frame continuously (XDamage fires, pixels do not)
//   --counter     large frame counter + top/bottom parity bars (debug frame order)
//   --popup       create a same-PID popup after 2s and close it after 3s
//   --size WxH    source size (default 1280x720)
//   --win-id-file PATH   write the window id immediately (for scripts)
//   --duration-ms N      exit automatically
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/Xatom.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <signal.h>
#include <time.h>

static volatile sig_atomic_t stop_flag = 0;
static void onsig(int s) { (void)s; stop_flag = 1; }
static long now_ms(void) { struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts); return ts.tv_sec * 1000L + ts.tv_nsec / 1000000L; }

static void draw_counter(Display *d, Window win, GC gc, int frame, int width, int height) {
    static const int segs[10][7] = {
        {1,1,1,1,1,1,0}, {0,1,1,0,0,0,0}, {1,1,0,1,1,0,1}, {1,1,1,1,0,0,1},
        {0,1,1,0,0,1,1}, {1,0,1,1,0,1,1}, {1,0,1,1,1,1,1}, {1,1,1,0,0,0,0},
        {1,1,1,1,1,1,1}, {1,1,1,1,0,1,1}
    };
    XSetForeground(d, gc, 0x000000u);
    XFillRectangle(d, win, gc, 0, 0, width, height);

    // Parity bars: a torn frame will show different top/bottom colors.
    XSetForeground(d, gc, (frame & 1) ? 0x0000ffu : 0xff0000u);
    XFillRectangle(d, win, gc, 0, 0, width, 10);
    XSetForeground(d, gc, (frame & 1) ? 0xff0000u : 0x0000ffu);
    XFillRectangle(d, win, gc, 0, height - 10, width, 10);

    int digits[4];
    int value = frame % 10000;
    for (int i = 3; i >= 0; i--) { digits[i] = value % 10; value /= 10; }

    int w = 96, h = 180, th = 16, gap = 18;
    int total = 4 * w + 3 * gap;
    int x0 = (width - total) / 2;
    int y0 = (height - h) / 2;
    for (int i = 0; i < 4; i++) {
        int x = x0 + i * (w + gap), y = y0;
        const int *s = segs[digits[i]];
        XSetForeground(d, gc, 0xffffffu);
        if (s[0]) XFillRectangle(d, win, gc, x + th, y, w - 2 * th, th);
        if (s[1]) XFillRectangle(d, win, gc, x + w - th, y + th, th, h / 2 - 2 * th);
        if (s[2]) XFillRectangle(d, win, gc, x + w - th, y + h / 2 + th, th, h / 2 - 2 * th);
        if (s[3]) XFillRectangle(d, win, gc, x + th, y + h - th, w - 2 * th, th);
        if (s[4]) XFillRectangle(d, win, gc, x, y + h / 2 + th, th, h / 2 - 2 * th);
        if (s[5]) XFillRectangle(d, win, gc, x, y + th, th, h / 2 - 2 * th);
        if (s[6]) XFillRectangle(d, win, gc, x + th, y + h / 2 - th / 2, w - 2 * th, th);
    }
}

int main(int argc, char **argv) {
    int mode_static = 0, mode_dirty = 0, mode_animate = 0, mode_repaint = 0, mode_counter = 0, popup = 0;
    int width = 1280, height = 720, delay_ms = 3000, duration_ms = 0;
    const char *win_id_file = NULL;
    for (int i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "--static")) mode_static = 1;
        else if (!strcmp(argv[i], "--dirty")) mode_dirty = 1;
        else if (!strcmp(argv[i], "--animate")) mode_animate = 1;
        else if (!strcmp(argv[i], "--repaint")) mode_repaint = 1;
        else if (!strcmp(argv[i], "--counter")) mode_counter = 1;
        else if (!strcmp(argv[i], "--popup")) popup = 1;
        else if (!strcmp(argv[i], "--size") && i + 1 < argc) sscanf(argv[++i], "%dx%d", &width, &height);
        else if (!strcmp(argv[i], "--delay-ms") && i + 1 < argc) delay_ms = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--duration-ms") && i + 1 < argc) duration_ms = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--win-id-file") && i + 1 < argc) win_id_file = argv[++i];
        else { fprintf(stderr, "unknown option %s\n", argv[i]); return 2; }
    }
    if (width < 64 || height < 64) return 2;
    signal(SIGTERM, onsig); signal(SIGINT, onsig);

    Display *d = XOpenDisplay(NULL);
    if (!d) { fprintf(stderr, "no display\n"); return 1; }
    int scr = DefaultScreen(d);
    int sw = DisplayWidth(d, scr), sh = DisplayHeight(d, scr);
    int x = (sw - width) / 2, y = (sh - height) / 2;
    if (x < 0) x = 0; if (y < 0) y = 0;
    Window win = XCreateSimpleWindow(d, RootWindow(d, scr), x, y, width, height, 0, 0, 0x101010);
    XStoreName(d, win, "M1b Test Pattern");
    XClassHint class_hint;
    class_hint.res_name = (char*)"testpattern";
    class_hint.res_class = (char*)"testpattern";
    XSetClassHint(d, win, &class_hint);
    Atom netpid = XInternAtom(d, "_NET_WM_PID", False);
    unsigned long pid = (unsigned long)getpid();
    XChangeProperty(d, win, netpid, XA_CARDINAL, 32, PropModeReplace, (unsigned char*)&pid, 1);
    XSelectInput(d, win, ExposureMask | ButtonPressMask | ButtonReleaseMask |
                        PointerMotionMask | StructureNotifyMask);
    XMapWindow(d, win); XFlush(d);
    for (int i = 0; i < 100 && !stop_flag; i++) {
        XEvent e; if (XCheckTypedWindowEvent(d, win, MapNotify, &e)) break; usleep(20000);
    }
    XSync(d, False);
    if (win_id_file) {
        FILE *f = fopen(win_id_file, "w");
        if (f) { fprintf(f, "0x%lx\n", win); fclose(f); }
    }
    printf("testpattern pid=%lu win=0x%lx size=%dx%d mode=%s%s%s%s%s popup=%d\n",
        pid, win, width, height,
        mode_static ? "static " : "", mode_dirty ? "dirty " : "",
        mode_animate ? "animate " : "", mode_repaint ? "repaint " : "",
        mode_counter ? "counter " : "", popup);
    fflush(stdout);

    GC gc = XCreateGC(d, win, 0, NULL);
    uint32_t *bg = (uint32_t*)malloc((size_t)width * height * 4);
    uint32_t *px = (uint32_t*)malloc((size_t)width * height * 4);
    if (!bg || !px) return 1;
    for (int yy = 0; yy < height; yy++) for (int xx = 0; xx < width; xx++) {
        uint8_t r = (uint8_t)(xx * 255 / width), g = (uint8_t)(yy * 255 / height), b = (uint8_t)((xx + yy) / 8);
        uint32_t v = 0xff000000u | ((uint32_t)r << 16) | ((uint32_t)g << 8) | b;
        if ((xx % 32) == 0 || (yy % 32) == 0) v = 0xff202020u;
        bg[(size_t)yy * width + xx] = v;
    }
    memcpy(px, bg, (size_t)width * height * 4);
    XImage *img = XCreateImage(d, DefaultVisual(d, scr), 24, ZPixmap, 0, (char*)px, width, height, 32, 0);
    if (!img) return 1;
    XPutImage(d, win, gc, img, 0, 0, 0, 0, width, height);
    XSetForeground(d, gc, 0xffffffffu);
    const char *label = "M1b test pattern: 1px grid + 64x64 block";
    XDrawString(d, win, gc, 24, 40, label, (int)strlen(label));
    XFlush(d);

    int mx = width / 2, my = height / 2, mw = 64, mh = 64;
    if (mode_static) {
        for (int yy = 0; yy < mh; yy++) for (int xx = 0; xx < mw; xx++)
            px[(size_t)(my + yy) * width + mx + xx] = 0xffff00ffu;
        XPutImage(d, win, gc, img, mx, my, mx, my, mw, mh); XFlush(d);
    }

    long start = now_ms();
    int frame = 0, dirty_done = 0, popup_shown = 0;
    Window popup_win = 0;
    int old_x = mx, old_y = my;
    while (!stop_flag) {
        long elapsed = now_ms() - start;
        if (duration_ms > 0 && elapsed >= duration_ms) break;
        if (popup && !popup_shown && elapsed >= 2000) {
            popup_win = XCreateSimpleWindow(d, RootWindow(d, scr), width / 3, height / 3, 320, 200, 0, 0, 0x202020);
            XStoreName(d, popup_win, "M1b Popup");
            XChangeProperty(d, popup_win, netpid, XA_CARDINAL, 32, PropModeReplace, (unsigned char*)&pid, 1);
            XMapRaised(d, popup_win); XFlush(d); popup_shown = 1;
        }
        if (popup && popup_shown && elapsed >= 5000) {
            XDestroyWindow(d, popup_win); XFlush(d); popup_win = 0; popup_shown = 0; popup = 0;
        }

        if (mode_counter) {
            draw_counter(d, win, gc, frame, width, height);
            XFlush(d);
            frame++;
            usleep(16000);
            continue;
        }
        if (mode_dirty && !dirty_done && elapsed >= delay_ms) {
            for (int yy = 0; yy < mh; yy++) for (int xx = 0; xx < mw; xx++)
                px[(size_t)(my + yy) * width + mx + xx] = 0xffff00ffu;
            XPutImage(d, win, gc, img, mx, my, mx, my, mw, mh);
            XFlush(d); dirty_done = 1;
        } else if (mode_animate) {
            int nx = 100 + (frame * 12) % (width - 200);
            int ny = 100 + (frame * 5) % (height - 200);
            int ux = old_x < nx ? old_x : nx, uy = old_y < ny ? old_y : ny;
            int ur = (old_x + mw > nx + mw ? old_x + mw : nx + mw);
            int ub = (old_y + mh > ny + mh ? old_y + mh : ny + mh);
            for (int yy = uy; yy < ub; yy++) for (int xx = ux; xx < ur; xx++)
                px[(size_t)yy * width + xx] = bg[(size_t)yy * width + xx];
            for (int yy = 0; yy < mh; yy++) for (int xx = 0; xx < mw; xx++)
                px[(size_t)(ny + yy) * width + nx + xx] = 0xffff00ffu;
            XPutImage(d, win, gc, img, ux, uy, ux, uy, ur - ux, ub - uy);
            XFlush(d); old_x = nx; old_y = ny; frame++;
        } else if (mode_repaint) {
            XPutImage(d, win, gc, img, 0, 0, 0, 0, width, height); XFlush(d);
        }
        usleep(16000);
    }

    if (popup_win) XDestroyWindow(d, popup_win);
    XDestroyImage(img); // frees px
    XFreeGC(d, gc); XDestroyWindow(d, win); XCloseDisplay(d);
    free(bg);
    return 0;
}
