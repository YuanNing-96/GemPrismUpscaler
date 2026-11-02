#!/usr/bin/env python3
"""Build the M0b shaders.

The Anime4K shader is distributed in mpv's user-shader format (//!HOOK, //!BIND,
//!SAVE, ...). This script expands its passes into standalone Vulkan compute
shaders, compiles every shader to SPIR-V with glslangValidator, validates the
result, and copies the .spv files into the Core assembly as embedded resources.

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
ANIME4K_SRC = ROOT / "shaders" / "anime4k" / "Anime4K_Upscale_CNN_x2_S.glsl"
GENERATED = ROOT / "shaders" / "generated"
SPV_OUT = ROOT / "src" / "GemPrismUpscaler.Core" / "Shaders"
LANCZOS = ROOT / "shaders" / "lanczos"

# The order here is the descriptor binding order in the generated compute shaders.
TEXTURE_NAMES = ["MAIN", "conv2d_tf", "conv2d_1_tf", "conv2d_2_tf", "conv2d_last_tf"]
PUSH_FIELDS = {
    "MAIN": "mainSize",
    "conv2d_tf": "conv0Size",
    "conv2d_1_tf": "conv1Size",
    "conv2d_2_tf": "conv2Size",
    "conv2d_last_tf": "conv3Size",
}
OUTPUT_NAMES = {
    "conv2d_tf": "anime4k_conv0",
    "conv2d_1_tf": "anime4k_conv1",
    "conv2d_2_tf": "anime4k_conv2",
    "conv2d_last_tf": "anime4k_conv3",
    "MAIN": "anime4k_d2s",  # final Depth-to-Space pass replaces MAIN
}


def directive(pass_, name: str) -> str | None:
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
    for m in re.finditer(r"\b([A-Za-z_]\w*)_(?:tex|texOff|pos|size|pt|raw)\b", body):
        name = m.group(1)
        if name in TEXTURE_NAMES and name not in used:
            used.append(name)
    return used


def generate_pass(pass_: dict) -> tuple[str, dict]:
    save = directive(pass_, "SAVE")
    width = directive(pass_, "WIDTH") or ""
    if save is None:
        raise SystemExit(f"pass without //!SAVE: {pass_['desc']}")
    out_name = OUTPUT_NAMES[save]
    scale = 2 if width.rstrip().endswith("2 *") else 1
    body = "\n".join(pass_["body"]).strip("\n")
    used = used_textures(body)

    decls = [f"layout(binding = {i}) uniform sampler2D {name}_sampler;" for i, name in enumerate(used)]
    decls.append(f"layout(rgba16f, binding = {len(used)}) uniform image2D outImage;")

    macros: list[str] = []
    for name in used:
        field = PUSH_FIELDS[name]
        macros.extend([
            f"#define {name}_pos HOOKED_pos",
            f"#define {name}_size pc.{field}",
            f"#define {name}_pt (1.0 / pc.{field})",
            f"#define {name}_tex(p) texture({name}_sampler, p)",
            f"#define {name}_texOff(o) texture({name}_sampler, HOOKED_pos + (o) * {name}_pt)",
        ])

    push = """layout(push_constant) uniform PC {
    vec2 outSize;
    vec2 mainSize;
    vec2 conv0Size;
    vec2 conv1Size;
    vec2 conv2Size;
    vec2 conv3Size;
} pc;"""

    shader = f"""#version 450
{HEADER}

layout(local_size_x = 8, local_size_y = 8) in;

{push}
{chr(10).join(decls)}
{chr(10).join(macros)}

vec2 HOOKED_pos;

{body}

void main() {{
    ivec2 p = ivec2(gl_GlobalInvocationID.xy);
    if (p.x >= int(pc.outSize.x) || p.y >= int(pc.outSize.y)) return;
    HOOKED_pos = (vec2(p) + vec2(0.5)) / pc.outSize;
    imageStore(outImage, p, hook());
}}
"""
    info = {
        "name": out_name,
        "desc": pass_["desc"],
        "inputs": used,
        "output": save,
        "scale": scale,
        "shader": f"{out_name}.comp",
        "spv": f"{out_name}.comp.spv",
    }
    return shader, info


def run(cmd: list[str]) -> None:
    proc = subprocess.run(cmd, text=True, capture_output=True)
    if proc.returncode != 0:
        print(proc.stdout)
        print(proc.stderr, file=sys.stderr)
        raise SystemExit(f"command failed: {' '.join(cmd)}")


HEADER = ""


def main() -> None:
    global HEADER
    for tool in ("glslangValidator", "spirv-val"):
        if shutil.which(tool) is None:
            raise SystemExit(f"{tool} not found; install the Vulkan SDK / glslang")

    GENERATED.mkdir(parents=True, exist_ok=True)
    SPV_OUT.mkdir(parents=True, exist_ok=True)

    text = ANIME4K_SRC.read_text()
    header, passes = parse_passes(text)
    HEADER = (
        "// Generated from Anime4K_Upscale_CNN_x2_S.glsl (MIT, Copyright (c) bloc97).\n"
        "// mpv user-shader directives were expanded by tools/build_shaders.py.\n"
        "// Do not edit by hand.\n\n" + header
    )

    manifest = []
    for pass_ in passes:
        shader, info = generate_pass(pass_)
        comp = GENERATED / info["shader"]
        comp.write_text(shader)
        spv = GENERATED / info["spv"]
        run(["glslangValidator", "-S", "comp", "-V", "--target-env", "vulkan1.2",
             "-o", str(spv), str(comp)])
        run(["spirv-val", str(spv)])
        shutil.copy2(spv, SPV_OUT / info["spv"])
        manifest.append(info)

    (GENERATED / "manifest.json").write_text(json.dumps({"passes": manifest}, indent=2) + "\n")

    for name in ("lanczos.vert", "lanczos.frag"):
        src = LANCZOS / name
        spv = SPV_OUT / f"{name}.spv"
        run(["glslangValidator", "-V", "--target-env", "vulkan1.2", "-o", str(spv), str(src)])
        run(["spirv-val", str(spv)])

    print("built shaders:")
    for p in sorted(SPV_OUT.glob("*.spv")):
        print(f"  {p.relative_to(ROOT)} ({p.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
