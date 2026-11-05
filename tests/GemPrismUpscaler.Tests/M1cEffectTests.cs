using GemPrismUpscaler.Core.Rendering;
using Xunit;

namespace GemPrismUpscaler.Tests;

public sealed class M1cEffectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "gemprism-effect-tests", Guid.NewGuid().ToString("N"));

    public M1cEffectTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ValidCustomEffect_Loads()
    {
        WriteSpirv("sharpen.spv");
        string toml = Path.Combine(_dir, "effect.toml");
        File.WriteAllText(toml, """
            abi = 1
            id = "sharpen-test"
            name = "Sharpen Test"
            radius = 2
            requires_full_frame = false

            [[pass]]
            name = "sharpen"
            shader = "sharpen.spv"
            inputs = ["MAIN"]
            output = "upscaled2x"
            output_scale = 2
            """);

        var effect = CustomEffectLoader.Load(toml, out string? error);

        Assert.Null(error);
        Assert.NotNull(effect);
        Assert.Equal("sharpen-test", effect!.Id);
        Assert.Equal(EffectKind.Custom, effect.Kind);
        Assert.True(effect.IsAvailable);
        Assert.NotNull(effect.Variant);
        Assert.Single(effect.Variant!.Passes);
        Assert.Collection(effect.Variant.Textures,
            t => Assert.Equal("MAIN", t.Name),
            t => Assert.Equal("upscaled2x", t.Name));
        Assert.Equal("upscaled2x", effect.Variant.OutputTexture);
        Assert.False(effect.RequiresFullFrame);
    }

    [Fact]
    public void MissingShader_Fails()
    {
        string toml = Path.Combine(_dir, "effect.toml");
        File.WriteAllText(toml, """
            abi = 1
            id = "missing"
            name = "Missing"

            [[pass]]
            shader = "nope.spv"
            inputs = ["MAIN"]
            output = "out"
            output_scale = 1
            """);

        var effect = CustomEffectLoader.Load(toml, out string? error);

        Assert.Null(effect);
        Assert.Contains("找不到 shader", error);
    }

    [Fact]
    public void UnknownInput_Fails()
    {
        WriteSpirv("x.spv");
        string toml = Path.Combine(_dir, "effect.toml");
        File.WriteAllText(toml, """
            abi = 1
            id = "bad-input"
            name = "Bad Input"

            [[pass]]
            shader = "x.spv"
            inputs = ["not-yet-saved"]
            output = "out"
            output_scale = 1
            """);

        var effect = CustomEffectLoader.Load(toml, out string? error);

        Assert.Null(effect);
        Assert.Contains("尚未生成", error);
    }

    [Fact]
    public void WrongAbi_Fails()
    {
        WriteSpirv("x.spv");
        string toml = Path.Combine(_dir, "effect.toml");
        File.WriteAllText(toml, """
            abi = 2
            id = "future"
            name = "Future"

            [[pass]]
            shader = "x.spv"
            inputs = ["MAIN"]
            output = "out"
            output_scale = 1
            """);

        var effect = CustomEffectLoader.Load(toml, out string? error);

        Assert.Null(effect);
        Assert.Contains("abi=2", error);
    }

    private void WriteSpirv(string name)
    {
        // The loader only checks the SPIR-V magic; the chain never runs in unit tests.
        File.WriteAllBytes(Path.Combine(_dir, name), [0x03, 0x02, 0x23, 0x07]);
    }
}
