using System.Reflection;
using Silk.NET.Vulkan;

namespace GemPrismUpscaler.Core.Rendering;

internal static unsafe class VulkanUtil
{
    public static void Check(Result result, string what)
    {
        if (result != Result.Success) throw new InvalidOperationException($"{what} failed: {result}");
    }

    public static uint FindMemoryType(Vk vk, PhysicalDevice physicalDevice, uint typeBits, MemoryPropertyFlags wanted)
    {
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out var props);
        for (uint i = 0; i < props.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) == 0) continue;
            if ((props.MemoryTypes[(int)i].PropertyFlags & wanted) == wanted) return i;
        }
        throw new InvalidOperationException("no suitable memory type");
    }

    public static byte[] LoadSpirv(string fileName)
    {
        var assembly = typeof(VulkanUtil).Assembly;
        string suffix = "." + fileName;
        string? resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
        if (resource is null)
            throw new InvalidOperationException(
                $"embedded shader '{fileName}' not found; run tools/build_shaders.py");
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"cannot open embedded shader '{resource}'");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static void ImageBarrier(Vk vk, CommandBuffer cmd, Image image,
        ImageLayout oldLayout, ImageLayout newLayout,
        AccessFlags srcAccess, AccessFlags dstAccess,
        PipelineStageFlags srcStage, PipelineStageFlags dstStage)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };
        vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }
}
