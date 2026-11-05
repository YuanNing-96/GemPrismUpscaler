using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Rendering;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class M1bShaderTests
{
    [Fact]
    public void VersionIs030()
    {
        Assert.Equal("0.3.0", AppInfo.Version);
        Assert.Equal("GemPrismUpscaler v0.3.0", AppInfo.DisplayName);
    }

    [Fact]
    public void ManifestContainsSmlVariantsInOrder()
    {
        var manifest = Anime4KShaders.Manifest;
        Assert.Equal(new[] { "s", "m", "l", "vl" }, manifest.VariantOrder);
        Assert.Equal(4, manifest.Variants.Count);
        Assert.Equal("Anime4K S", manifest.Variants["s"].DisplayName);
        Assert.Equal("Anime4K M", manifest.Variants["m"].DisplayName);
        Assert.Equal("Anime4K L", manifest.Variants["l"].DisplayName);
        Assert.Equal("Anime4K VL", manifest.Variants["vl"].DisplayName);
    }

    [Theory]
    [InlineData("s", 5, 6, 5)]
    [InlineData("m", 9, 10, 8)]
    [InlineData("l", 10, 11, 5)]
    [InlineData("vl", 18, 19, 8)]
    public void VariantShapeMatchesTheExpandedShader(string key, int passes, int textures, int radius)
    {
        var variant = Anime4KShaders.Manifest.Variants[key];
        Assert.Equal(passes, variant.Passes.Count);
        Assert.Equal(textures, variant.Textures.Count);
        Assert.Equal(textures, variant.TextureCount);
        Assert.Equal(radius, variant.Radius);
        Assert.Equal("upscaled2x", variant.OutputTexture);

        var output = variant.Textures.Single(t => t.Name == variant.OutputTexture);
        Assert.Equal(2, output.Scale);
        Assert.False(output.External);
        Assert.Equal(1, variant.Textures.Single(t => t.Name == "MAIN").Scale);
    }

    [Fact]
    public void EveryVariantShaderIsEmbeddedAndValidSpirv()
    {
        foreach (var variant in Anime4KShaders.Manifest.Variants.Values)
        {
            foreach (var pass in variant.Passes)
                AssertSpirv(pass.Shader);
        }

        AssertSpirv("lanczos.vert.spv");
        AssertSpirv("lanczos.frag.spv");
    }

    [Fact]
    public void PassGraphIsTopologicallyOrdered()
    {
        foreach (var variant in Anime4KShaders.Manifest.Variants.Values)
        {
            var available = new HashSet<string> { "MAIN" };
            var outputs = new HashSet<string> { "MAIN" };
            foreach (var pass in variant.Passes)
            {
                foreach (string input in pass.Inputs)
                    Assert.Contains(input, available);
                Assert.True(outputs.Add(pass.Output), $"{variant.DisplayName}: duplicate output {pass.Output}");
                available.Add(pass.Output);
            }
            Assert.Contains(variant.OutputTexture, available);
        }
    }

    [Fact]
    public void PushConstantBlocksFitTheVulkanLimits()
    {
        foreach (var variant in Anime4KShaders.Manifest.Variants.Values)
        {
            int expected = 3 * 8 + variant.Textures.Count * 8;
            Assert.Equal(expected, variant.PushConstantBytes);
            Assert.True(variant.PushConstantBytes <= 256,
                $"{variant.DisplayName}: {variant.PushConstantBytes} bytes");
            if (variant.DisplayName != "Anime4K VL")
                Assert.True(variant.PushConstantBytes <= 128,
                    $"{variant.DisplayName}: {variant.PushConstantBytes} bytes");
        }
    }

    [Theory]
    [InlineData("anime4k", EffectMode.Anime4KS)]
    [InlineData("anime4k-s", EffectMode.Anime4KS)]
    [InlineData("anime4k-m", EffectMode.Anime4KM)]
    [InlineData("anime4k-l", EffectMode.Anime4KL)]
    [InlineData("anime4k-vl", EffectMode.Anime4KVL)]
    [InlineData("bilinear", EffectMode.Bilinear)]
    public void EffectAliasesParse(string value, EffectMode expected)
    {
        Assert.True(EffectModeExtensions.TryParse(value, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("anime4k-xl")]
    [InlineData("fsr")]
    public void UnknownEffectIsRejected(string value)
    {
        Assert.False(EffectModeExtensions.TryParse(value, out _));
    }

    private static void AssertSpirv(string name)
    {
        byte[] spirv = VulkanUtil.LoadSpirv(name);
        Assert.True(spirv.Length > 4, name);
        // SPIR-V magic number, little endian.
        Assert.Equal(0x03, spirv[0]);
        Assert.Equal(0x02, spirv[1]);
        Assert.Equal(0x23, spirv[2]);
        Assert.Equal(0x07, spirv[3]);
    }
}