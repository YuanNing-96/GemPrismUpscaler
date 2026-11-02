namespace GemPrismUpscaler.Core.Rendering;

public enum EffectMode
{
    /// <summary>M0a path: one vkCmdBlitImage with linear filtering.</summary>
    Bilinear = 0,

    /// <summary>M0b path: Anime4K 2x CNN (S) + Lanczos-2 final composition.</summary>
    Anime4K = 1
}
