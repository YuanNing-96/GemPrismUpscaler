using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Rendering;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class M0bTests
{
    [Fact]
    public void VersionIs020()
    {
        Assert.Equal("0.2.0", AppInfo.Version);
        Assert.Equal("GemPrismUpscaler v0.2.0", AppInfo.DisplayName);
    }

    [Theory]
    [InlineData("anime4k_conv0.comp.spv")]
    [InlineData("anime4k_conv1.comp.spv")]
    [InlineData("anime4k_conv2.comp.spv")]
    [InlineData("anime4k_conv3.comp.spv")]
    [InlineData("anime4k_d2s.comp.spv")]
    [InlineData("lanczos.vert.spv")]
    [InlineData("lanczos.frag.spv")]
    public void ShaderIsEmbedded(string name)
    {
        byte[] spirv = VulkanUtil.LoadSpirv(name);
        Assert.True(spirv.Length > 4);
        // SPIR-V magic number, little endian.
        Assert.Equal(0x03, spirv[0]);
        Assert.Equal(0x02, spirv[1]);
        Assert.Equal(0x23, spirv[2]);
        Assert.Equal(0x07, spirv[3]);
    }
}
