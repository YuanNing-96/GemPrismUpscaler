// M1b zero-copy spike, part 2: take a window pixmap's DMA-BUF via DRI3 and try
// to import it as a linear Vulkan image on the NVIDIA device, then copy one
// frame back to a host buffer. This is a feasibility probe, not production code.
#include <xcb/xcb.h>
#include <xcb/dri3.h>
#include <xcb/composite.h>
#include <vulkan/vulkan.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <unistd.h>

#define CHECK(x, msg) do { VkResult _r = (x); if (_r != VK_SUCCESS) { \
    fprintf(stderr, "%s failed: %d\n", msg, _r); return 1; } } while (0)

static PFN_vkGetMemoryFdPropertiesKHR pGetMemoryFdProperties;

int main(int argc, char **argv) {
    if (argc < 2) { fprintf(stderr, "usage: %s <window-id>\n", argv[0]); return 2; }
    uint32_t win = (uint32_t)strtoul(argv[1], NULL, 0);

    int screen = 0;
    xcb_connection_t *c = xcb_connect(NULL, &screen);
    if (xcb_connection_has_error(c)) { fprintf(stderr, "xcb connect failed\n"); return 1; }
    const xcb_query_extension_reply_t *dri3 = xcb_get_extension_data(c, &xcb_dri3_id);
    const xcb_query_extension_reply_t *comp = xcb_get_extension_data(c, &xcb_composite_id);
    if (!dri3 || !dri3->present || !comp || !comp->present) return 1;

    xcb_pixmap_t pixmap = xcb_generate_id(c);
    xcb_generic_error_t *err = xcb_request_check(c,
        xcb_composite_name_window_pixmap_checked(c, win, pixmap));
    if (err) { printf("NameWindowPixmap failed %u\n", err->error_code); return 1; }

    xcb_dri3_buffers_from_pixmap_reply_t *breply =
        xcb_dri3_buffers_from_pixmap_reply(c, xcb_dri3_buffers_from_pixmap(c, pixmap), &err);
    if (!breply) { printf("BuffersFromPixmap failed %u\n", err ? err->error_code : 0); return 1; }
    printf("DRI3 export: nfd=%u %ux%u depth=%u bpp=%u modifier=0x%llx\n",
        breply->nfd, breply->width, breply->height, breply->depth, breply->bpp,
        (unsigned long long)breply->modifier);
    if (breply->nfd != 1 || breply->modifier != 0) {
        printf("NOT LINEAR or multi-plane; this probe only imports linear 1-fd buffers\n");
        return 0;
    }
    int32_t *fds = xcb_dri3_buffers_from_pixmap_reply_fds(c, breply);
    int fd = fds[0];
    uint32_t width = breply->width, height = breply->height;
    int stride = (int)((width * 4 + 255) & ~255u); // match the legacy stride probe
    printf("import fd=%d stride guess=%d\n", fd, stride);

    VkApplicationInfo app = { .sType = VK_STRUCTURE_TYPE_APPLICATION_INFO,
        .pApplicationName = "dri3vkprobe", .apiVersion = VK_API_VERSION_1_2 };
    const char *instExts[] = { "VK_KHR_external_memory_capabilities" };
    VkInstanceCreateInfo ici = { .sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
        .pApplicationInfo = &app, .enabledExtensionCount = 1, .ppEnabledExtensionNames = instExts };
    VkInstance inst;
    CHECK(vkCreateInstance(&ici, NULL, &inst), "vkCreateInstance");

    uint32_t n = 0;
    vkEnumeratePhysicalDevices(inst, &n, NULL);
    VkPhysicalDevice *phys = calloc(n, sizeof(*phys));
    vkEnumeratePhysicalDevices(inst, &n, phys);
    VkPhysicalDevice gpu = VK_NULL_HANDLE;
    for (uint32_t i = 0; i < n; i++) {
        VkPhysicalDeviceProperties p; vkGetPhysicalDeviceProperties(phys[i], &p);
        printf("device %u: %s\n", i, p.deviceName);
        if (p.vendorID == 0x10DE) gpu = phys[i];
    }
    if (gpu == VK_NULL_HANDLE) { printf("no NVIDIA device\n"); return 1; }

    uint32_t qn = 0; vkGetPhysicalDeviceQueueFamilyProperties(gpu, &qn, NULL);
    VkQueueFamilyProperties *q = calloc(qn, sizeof(*q));
    vkGetPhysicalDeviceQueueFamilyProperties(gpu, &qn, q);
    uint32_t qf = 0; for (uint32_t i = 0; i < qn; i++) if (q[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) { qf = i; break; }

    const char *devExts[] = { "VK_KHR_external_memory", "VK_KHR_external_memory_fd",
        "VK_EXT_external_memory_dma_buf", "VK_EXT_image_drm_format_modifier" };
    VkDeviceQueueCreateInfo qci = { .sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
        .queueFamilyIndex = qf, .queueCount = 1, .pQueuePriorities = (float[]){1.0f} };
    VkDeviceCreateInfo dci = { .sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
        .queueCreateInfoCount = 1, .pQueueCreateInfos = &qci,
        .enabledExtensionCount = 4, .ppEnabledExtensionNames = devExts };
    VkDevice dev;
    CHECK(vkCreateDevice(gpu, &dci, NULL, &dev), "vkCreateDevice");
    pGetMemoryFdProperties = (PFN_vkGetMemoryFdPropertiesKHR)vkGetDeviceProcAddr(dev, "vkGetMemoryFdPropertiesKHR");

    VkPhysicalDeviceExternalImageFormatInfo extInfo = {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_IMAGE_FORMAT_INFO,
        .handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT };
    VkPhysicalDeviceImageDrmFormatModifierInfoEXT modInfo = {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_DRM_FORMAT_MODIFIER_INFO_EXT,
        .drmFormatModifier = 0, .sharingMode = VK_SHARING_MODE_EXCLUSIVE };
    VkPhysicalDeviceImageFormatInfo2 fmtInfo = {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_FORMAT_INFO_2,
        .format = VK_FORMAT_B8G8R8A8_UNORM, .type = VK_IMAGE_TYPE_2D,
        .tiling = VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT,
        .usage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT,
        .pNext = &extInfo };
    extInfo.pNext = &modInfo;
    VkExternalImageFormatProperties extProps = { .sType = VK_STRUCTURE_TYPE_EXTERNAL_IMAGE_FORMAT_PROPERTIES };
    VkImageFormatProperties2 props = { .sType = VK_STRUCTURE_TYPE_IMAGE_FORMAT_PROPERTIES_2, .pNext = &extProps };
    VkResult fr = vkGetPhysicalDeviceImageFormatProperties2(gpu, &fmtInfo, &props);
    printf("image format properties: %d (ext features=0x%x)\n", fr,
        extProps.externalMemoryProperties.externalMemoryFeatures);
    if (fr != VK_SUCCESS) return 0;

    VkSubresourceLayout plane = { .offset = 0, .size = 0, .rowPitch = (uint64_t)stride,
        .arrayPitch = 0, .depthPitch = 0 };
    VkImageDrmFormatModifierExplicitCreateInfoEXT modExplicit = {
        .sType = VK_STRUCTURE_TYPE_IMAGE_DRM_FORMAT_MODIFIER_EXPLICIT_CREATE_INFO_EXT,
        .drmFormatModifier = 0, .drmFormatModifierPlaneCount = 1, .pPlaneLayouts = &plane };
    VkExternalMemoryImageCreateInfo extImage = {
        .sType = VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO,
        .handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT,
        .pNext = &modExplicit };
    VkImageCreateInfo imageInfo = { .sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
        .pNext = &extImage, .imageType = VK_IMAGE_TYPE_2D, .format = VK_FORMAT_B8G8R8A8_UNORM,
        .extent = { width, height, 1 }, .mipLevels = 1, .arrayLayers = 1,
        .samples = VK_SAMPLE_COUNT_1_BIT, .tiling = VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT,
        .usage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT,
        .sharingMode = VK_SHARING_MODE_EXCLUSIVE, .initialLayout = VK_IMAGE_LAYOUT_UNDEFINED };
    VkImage image;
    VkResult ir = vkCreateImage(dev, &imageInfo, NULL, &image);
    printf("vkCreateImage: %d\n", ir);
    if (ir != VK_SUCCESS) return 0;

    VkMemoryRequirements req; vkGetImageMemoryRequirements(dev, image, &req);
    printf("memory req: size=%llu typeBits=0x%x\n", (unsigned long long)req.size, req.memoryTypeBits);
    VkMemoryFdPropertiesKHR fdProps = { .sType = VK_STRUCTURE_TYPE_MEMORY_FD_PROPERTIES_KHR };
    if (pGetMemoryFdProperties(dev, VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT, fd, &fdProps) == VK_SUCCESS)
        printf("fd memory type bits=0x%x\n", fdProps.memoryTypeBits);

    VkImportMemoryFdInfoKHR importInfo = { .sType = VK_STRUCTURE_TYPE_IMPORT_MEMORY_FD_INFO_KHR,
        .handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT, .fd = fd };
    VkMemoryDedicatedAllocateInfo dedicated = { .sType = VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO,
        .image = image, .pNext = &importInfo };
    VkMemoryAllocateInfo alloc = { .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
        .pNext = &dedicated, .allocationSize = req.size, .memoryTypeIndex = 0 };
    uint32_t mt = 0; while (!(req.memoryTypeBits & (1u << mt))) mt++;
    alloc.memoryTypeIndex = mt;
    VkDeviceMemory mem;
    VkResult ar = vkAllocateMemory(dev, &alloc, NULL, &mem);
    printf("vkAllocateMemory(import fd): %d\n", ar);
    if (ar == VK_SUCCESS) {
        VkResult br = vkBindImageMemory(dev, image, mem, 0);
        printf("vkBindImageMemory: %d => %s\n", br, br == VK_SUCCESS ? "IMPORT OK" : "IMPORT FAILED");
    }
    vkDestroyImage(dev, image, NULL);
    vkDestroyDevice(dev, NULL);
    vkDestroyInstance(inst, NULL);
    xcb_disconnect(c);
    return 0;
}
