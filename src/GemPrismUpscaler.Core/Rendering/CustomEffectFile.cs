using Tomlyn;

namespace GemPrismUpscaler.Core.Rendering;

public sealed class CustomEffectFile
{
    public int Abi { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int? OutputScale { get; set; }
    public int Radius { get; set; }
    public bool RequiresFullFrame { get; set; }
    public List<CustomTextureFile> Texture { get; set; } = new();
    public List<CustomPassFile> Pass { get; set; } = new();
}

public sealed class CustomTextureFile
{
    public string Name { get; set; } = string.Empty;
    public int Scale { get; set; } = 1;
    public bool External { get; set; }
}

public sealed class CustomPassFile
{
    public string Name { get; set; } = string.Empty;
    public string Shader { get; set; } = string.Empty;
    public List<string> Inputs { get; set; } = new();
    public string Output { get; set; } = string.Empty;
    public int OutputScale { get; set; } = 1;
    public List<int> Workgroup { get; set; } = new();
}

internal static class CustomEffectLoader
{
    private static readonly TomlSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = TomlIgnoreCondition.WhenWritingNull
    };

    public static EffectDefinition? Load(string path, out string? error)
    {
        error = null;
        try
        {
            string text = File.ReadAllText(path);
            var file = TomlSerializer.Deserialize<CustomEffectFile>(text, Options);
            if (file is null)
            {
                error = "TOML 内容为空";
                return null;
            }

            string directory = Path.GetDirectoryName(path) ?? ".";
            var variant = BuildVariant(file, directory, out error);
            if (variant is null) return null;

            return new EffectDefinition
            {
                Id = file.Id,
                DisplayName = string.IsNullOrWhiteSpace(file.Name) ? file.Id : file.Name,
                Kind = EffectKind.Custom,
                Mode = EffectMode.Custom,
                Variant = variant
            };
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static Anime4KVariant? BuildVariant(CustomEffectFile file, string directory, out string? error)
    {
        error = null;
        if (file.Abi != 1)
        {
            error = $"不支持的 abi={file.Abi}（当前支持 1）";
            return null;
        }
        if (string.IsNullOrWhiteSpace(file.Id))
        {
            error = "缺少 id";
            return null;
        }
        if (file.Pass.Count == 0)
        {
            error = "至少需要一个 [[pass]]";
            return null;
        }

        var textures = new List<Anime4KTexture>
        {
            new() { Name = "MAIN", Scale = 1, External = true }
        };
        var textureMap = new Dictionary<string, Anime4KTexture>(StringComparer.Ordinal)
        {
            ["MAIN"] = textures[0]
        };

        foreach (var texture in file.Texture)
        {
            if (string.IsNullOrWhiteSpace(texture.Name) || texture.Name == "MAIN")
            {
                error = "texture.name 不能为空，也不能是 MAIN";
                return null;
            }
            if (texture.Scale is < 1 or > 4)
            {
                error = $"texture '{texture.Name}' 的 scale={texture.Scale} 无效（1-4）";
                return null;
            }
            if (texture.External)
            {
                error = $"只有 MAIN 可以作为 external 纹理（'{texture.Name}'）";
                return null;
            }
            if (!textureMap.TryAdd(texture.Name, new Anime4KTexture
                {
                    Name = texture.Name,
                    Scale = texture.Scale,
                    External = false
                }))
            {
                error = $"纹理 '{texture.Name}' 重复";
                return null;
            }
            textures.Add(textureMap[texture.Name]);
        }

        var passes = new List<Anime4KPass>();
        var saved = new HashSet<string>(StringComparer.Ordinal) { "MAIN" };
        for (int i = 0; i < file.Pass.Count; i++)
        {
            var source = file.Pass[i];
            if (string.IsNullOrWhiteSpace(source.Shader))
            {
                error = $"pass[{i}] 缺少 shader";
                return null;
            }
            if (string.IsNullOrWhiteSpace(source.Output) || source.Output == "MAIN")
            {
                error = $"pass[{i}] 的 output 不能为空或 MAIN";
                return null;
            }
            if (source.OutputScale is < 1 or > 4)
            {
                error = $"pass[{i}] 的 output_scale={source.OutputScale} 无效（1-4）";
                return null;
            }
            if (source.Inputs.Count == 0)
            {
                error = $"pass[{i}] 至少需要一个 input";
                return null;
            }
            foreach (string input in source.Inputs)
            {
                if (!saved.Contains(input))
                {
                    error = $"pass[{i}] 读取了尚未生成的纹理 '{input}'";
                    return null;
                }
            }
            if (saved.Contains(source.Output))
            {
                error = $"纹理 '{source.Output}' 被多个 pass 写入";
                return null;
            }
            if (textureMap.TryGetValue(source.Output, out var declared))
            {
                if (declared.Scale != source.OutputScale)
                {
                    error = $"纹理 '{source.Output}' 声明为 {declared.Scale}×，但 pass 写入 {source.OutputScale}×";
                    return null;
                }
            }
            else
            {
                var added = new Anime4KTexture
                {
                    Name = source.Output,
                    Scale = source.OutputScale,
                    External = false
                };
                textures.Add(added);
                textureMap[source.Output] = added;
            }

            string shaderPath = Path.Combine(directory, source.Shader);
            if (!File.Exists(shaderPath))
            {
                error = $"找不到 shader '{source.Shader}'";
                return null;
            }
            if (!IsSpirv(shaderPath))
            {
                error = $"'{source.Shader}' 不是有效的 SPIR-V 文件";
                return null;
            }
            saved.Add(source.Output);

            passes.Add(new Anime4KPass
            {
                Index = i,
                Desc = string.IsNullOrWhiteSpace(source.Name) ? $"pass{i}" : source.Name,
                Inputs = source.Inputs,
                Output = source.Output,
                OutputScale = source.OutputScale,
                Shader = source.Shader
            });
        }

        if (textures.Count > 19)
        {
            error = $"纹理数 {textures.Count} 超过上限 19";
            return null;
        }
        if (passes.Any(p => p.Inputs.Count > 16))
        {
            error = "单个 pass 的输入超过上限 16";
            return null;
        }

        int pushBytes = 3 * 8 + textures.Count * 8;
        if (pushBytes > 256)
        {
            error = $"push constant {pushBytes}B 超过上限 256B";
            return null;
        }

        string output = passes[^1].Output;
        int outputScale = passes[^1].OutputScale;
        if (file.OutputScale is { } declaredScale && declaredScale != outputScale)
        {
            error = $"output_scale={declaredScale} 与最后一个 pass 的 {outputScale}× 不一致";
            return null;
        }

        return new Anime4KVariant
        {
            DisplayName = string.IsNullOrWhiteSpace(file.Name) ? file.Id : file.Name,
            Source = file.Id,
            Radius = Math.Max(0, file.Radius),
            TextureCount = textures.Count,
            PushConstantBytes = pushBytes,
            Textures = textures,
            OutputTexture = output,
            Passes = passes,
            ShaderDirectory = directory,
            RequiresFullFrame = file.RequiresFullFrame
        };
    }

    private static bool IsSpirv(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            if (stream.Read(header) != 4) return false;
            uint magic = BitConverter.ToUInt32(header);
            return magic == 0x07230203;
        }
        catch
        {
            return false;
        }
    }
}
