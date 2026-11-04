#!/usr/bin/env python3
"""Build the Anime4K S/M/L compute shaders and the shared manifest.

The Anime4K shaders are distributed in mpv's user-shader format (//!HOOK,
//!BIND, //!SAVE, ...). This script expands every variant into standalone Vulkan
compute shaders, compiles them to SPIR-V with glslangValidator, validates the
result, and copies both the .spv files and the manifest into the Core assembly
as embedded resources.

The generated shaders use a single, variant-independent push constant block:

    vec2 outSize;        // full output size of the pass
    vec2 regionOffset;   // top-left pixel of the dispatched region
    vec2 regionSize;     // dispatched region size in pixels
    vec2 texSize[N];     // full size of every bound texture, in manifest order

`regionOffset`/`regionSize` are the hooks used by M1b's dirty-rectangle work:
a full-frame render dispatches (0, 0, outSize), a partial render dispatches the
expanded dirty rectangle.

Run from the repository root:
    python3 tools/build_shaders.py
"""
from __future__ import annotations

import json
import pathlib
import re
import shutil
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
ANIME4K = ROOT / "shaders" / "anime4k"
GENERATED = ROOT / "shaders" / "generated"
SPV_OUT = ROOT / "src" / "GemPrismUpscaler.Core" / "Shaders"
LANCZOS = ROOT / "shaders" / "lanczos"

VARIANTS = {
    "s": {"file": "Anime4K_Upscale_CNN_x2_S.glsl", "display": "Anime4K S"},
    "m": {"file": "Anime4K_Upscale_CNN_x2_M.glsl", "display": "Anime4K M"},
    "l": {"file": "Anime4K_Upscale_CNN_x2_L.glsl", "display": "Anime4K L"},
}

# Upper bound supported by the generated push-constant block: 3 vec2 + N vec2.
MAX_TEXTURES = 13
PUSH_CONSTANT_BYTES = 128

TEXTURE_REF = re.compile(
    r"\b([A-Za-z_]\w*)_(?:tex|texOff|pos|size|pt|raw)\b"
)


def directive(pass_: dict, name: str) -> str | None:
    for d in pass_["dirs"]:
        if d.split()[0] == name:
            return d[len(name):].strip()
    return None


def parse_passes(text: str) -> tuple[str, list[dict]]:
    lines = text.splitlines()
    first = next(i for i, l in enumerate(lines) if l.startswith("//!DESC"))
    header = "\n".join(lines[:first]).strip()
    passes: list[dict] = []
    current: dict | None = None
    for line in lines[first:]:
        if line.startswith("//!DESC"):
            current = {"desc": line[len("//!DESC"):].strip(), "dirs": [], "body": []}
            passes.append(current)
        elif current is not None:
            if line.startswith("//!"):
                current["dirs"].append(line[3:].strip())
            else:
                current["body"].append(line)
    return header, passes


def used_textures(body: str) -> list[str]:
    used: list[str] = []
    for match in TEXTURE_REF.finditer(body):
        name = match.group(1)
        if name not in used:
            used.append(name)
    return used


def output_scale(pass_: dict) -> int:
    expr = (directive(pass_, "WIDTH") or "") + " " + (directive(pass_, "HEIGHT") or "")
    match = re.search(r"\b(\d+)\s*\*", expr)
    if match is None:
        return 1
    scale = int(match.group(1))
    if scale not in (1, 2):
        raise SystemExit(f"unsupported output scale {scale}: {pass_['desc']}")
    return scale


def generate_shader(variant: str, index: int, header: str, body: str,
                    inputs: list[str], textures: list[dict], source_file: str) -> str:
    texture_index = {t["name"]: i for i, t in enumerate(textures)}
    decls = [
        f"layout(binding = {binding}) uniform sampler2D {name}_sampler;"
        for binding, name in enumerate(inputs)
    ]
    decls.append(
        f"layout(rgba16f, binding = {len(inputs)}) uniform image2D outImage;"
    )

    macros: list[str] = []
    for name in inputs:
        idx = texture_index[name]
        macros.extend([
            f"#define {name}_pos HOOKED_pos",
            f"#define {name}_size pc.texSize[{idx}]",
            f"#define {name}_pt (1.0 / pc.texSize[{idx}])",
            f"#define {name}_tex(p) texture({name}_sampler, p)",
            f"#define {name}_texOff(o) texture({name}_sampler, HOOKED_pos + (o) * {name}_pt)",
        ])

    push = f"""layout(push_constant) uniform PC {{
    vec2 outSize;
    vec2 regionOffset;
    vec2 regionSize;
    vec2 texSize[{len(textures)}];
}} pc;"""

    return f"""#version 450
// Generated from {source_file} (Anime4K, MIT, Copyright (c) bloc97).
// Expanded by tools/build_shaders.py for variant '{variant}', pass {index}.
// Do not edit by hand.

{header}

layout(local_size_x = 8, local_size_y = 8) in;

{push}
{chr(10).join(decls)}
{chr(10).join(macros)}

vec2 HOOKED_pos;

{body.strip(chr(10))}

void main() {{
    ivec2 local = ivec2(gl_GlobalInvocationID.xy);
    ivec2 regionSize = ivec2(pc.regionSize);
    if (local.x >= regionSize.x || local.y >= regionSize.y) return;
    ivec2 p = ivec2(pc.regionOffset) + local;
    HOOKED_pos = (vec2(p) + vec2(0.5)) / pc.outSize;
    imageStore(outImage, p, hook());
}}
"""


def run(cmd: list[str]) -> None:
    proc = subprocess.run(cmd, text=True, capture_output=True)
    if proc.returncode != 0:
        print(proc.stdout)
        print(proc.stderr, file=sys.stderr)
        raise SystemExit(f"command failed: {' '.join(cmd)}")


def build_variant(variant: str, meta: dict) -> dict:
    source_file = meta["file"]
    text = (ANIME4K / source_file).read_text()
    header, passes = parse_passes(text)
    if not passes:
        raise SystemExit(f"no passes in {source_file}")

    textures: list[dict] = [{"name": "MAIN", "scale": 1, "external": True}]
    texture_index = {"MAIN": 0}
    radius = {"MAIN": 0}
    saved: set[str] = set()
    manifest_passes: list[dict] = []

    for index, pass_ in enumerate(passes):
        save = directive(pass_, "SAVE")
        if save is None:
            raise SystemExit(f"pass without //!SAVE: {pass_['desc']}")
        if save == "MAIN" and index != len(passes) - 1:
            raise SystemExit(f"only the final pass may save MAIN: {pass_['desc']}")

        scale = output_scale(pass_)
        output = "upscaled2x" if save == "MAIN" else save
        body = "\n".join(pass_["body"]).strip("\n")
        inputs = used_textures(body)
        if not inputs:
            raise SystemExit(f"pass without texture inputs: {pass_['desc']}")

        for name in inputs:
            if name == "MAIN":
                continue
            if name not in saved:
                raise SystemExit(
                    f"{source_file}: pass '{pass_['desc']}' reads '{name}' "
                    "before it is saved")
        if output in texture_index:
            raise SystemExit(f"{source_file}: duplicate output texture '{output}'")

        base_radius = max(radius[name] for name in inputs)
        pass_radius = base_radius + (1 if "x3x3" in pass_["desc"] else 0)

        textures.append({"name": output, "scale": scale, "external": False})
        texture_index[output] = len(textures) - 1
        radius[output] = pass_radius
        saved.add(save)

        shader_base = f"anime4k_{variant}_p{index}"
        shader = generate_shader(variant, index, header, body, inputs, textures, source_file)
        comp = GENERATED / f"{shader_base}.comp"
        comp.write_text(shader)
        spv = GENERATED / f"{shader_base}.comp.spv"
        run(["glslangValidator", "-S", "comp", "-V", "--target-env", "vulkan1.2",
             "-o", str(spv), str(comp)])
        run(["spirv-val", str(spv)])
        shutil.copy2(spv, SPV_OUT / f"{shader_base}.comp.spv")

        manifest_passes.append({
            "index": index,
            "desc": pass_["desc"],
            "inputs": inputs,
            "output": output,
            "outputScale": scale,
            "shader": f"{shader_base}.comp.spv",
        })

    output_texture = manifest_passes[-1]["output"]
    if output_texture != "upscaled2x":
        raise SystemExit(f"{source_file}: final pass output is '{output_texture}'")
    if len(textures) > MAX_TEXTURES:
        raise SystemExit(
            f"{source_file}: {len(textures)} textures exceed the {MAX_TEXTURES} limit")

    push_bytes = 3 * 8 + len(textures) * 8
    if push_bytes > PUSH_CONSTANT_BYTES:
        raise SystemExit(
            f"{source_file}: push constant block is {push_bytes} bytes "
            f"(limit {PUSH_CONSTANT_BYTES})")

    return {
        "displayName": meta["display"],
        "source": source_file,
        "radius": radius[output_texture] + 1,
        "textureCount": len(textures),
        "pushConstantBytes": push_bytes,
        "textures": textures,
        "outputTexture": output_texture,
        "passes": manifest_passes,
    }


def main() -> None:
    for tool in ("glslangValidator", "spirv-val"):
        if shutil.which(tool) is None:
            raise SystemExit(f"{tool} not found; install the Vulkan SDK / glslang")

    GENERATED.mkdir(parents=True, exist_ok=True)
    SPV_OUT.mkdir(parents=True, exist_ok=True)

    # Drop the previous generated set so removed/renamed passes cannot linger.
    for directory in (GENERATED, SPV_OUT):
        for path in directory.glob("anime4k_*"):
            if path.is_file():
                path.unlink()

    variants: dict[str, dict] = {}
    for key, meta in VARIANTS.items():
        variants[key] = build_variant(key, meta)

    manifest = {
        "generatedBy": "tools/build_shaders.py",
        "variantOrder": list(VARIANTS),
        "variants": variants,
    }
    payload = json.dumps(manifest, indent=2) + "\n"
    (GENERATED / "anime4k_manifest.json").write_text(payload)
    (GENERATED / "manifest.json").write_text(payload)
    (SPV_OUT / "anime4k_manifest.json").write_text(payload)

    for name in ("lanczos.vert", "lanczos.frag"):
        src = LANCZOS / name
        spv = SPV_OUT / f"{name}.spv"
        run(["glslangValidator", "-V", "--target-env", "vulkan1.2", "-o", str(spv), str(src)])
        run(["spirv-val", str(spv)])

    print("built shaders:")
    for p in sorted(SPV_OUT.glob("*.spv")):
        print(f"  {p.relative_to(ROOT)} ({p.stat().st_size} bytes)")
    print("variants:")
    for key, variant in variants.items():
        print(f"  {key}: {len(variant['passes'])} passes, "
              f"{variant['textureCount']} textures, radius={variant['radius']}, "
              f"push={variant['pushConstantBytes']}B")


if __name__ == "__main__":
    main()