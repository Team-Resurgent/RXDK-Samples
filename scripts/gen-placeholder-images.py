#!/usr/bin/env python3
"""Regenerate the placeholder Xbox title/save images and wire them into every sample.

Every Xbox title should carry a title image (128x128 DXT1) and default save image (64x64 DXT1),
both XPR0 (.xbx). Most ported samples shipped neither. This produces them:

  * neither image  -> the shared RXDK icon (Common/rxdk-titleimage.xbx + rxdk-saveimage.xbx),
                      referenced by relative path from each sample.
  * title but no save -> a 64x64 save DERIVED from the sample's own title (DXT1-decode -> downscale
                      -> re-encode), so the existing art is reused.
  * both already      -> left as-is.

Then it sets RxdkTitleImage / RxdkDefaultSaveImage in each .vcxproj (both Debug|Xbox and
Release|Xbox). After running, regenerate manifests + solutions (scripts/Generate-Manifests.ps1).

Needs: Pillow, and the RXDK Bundler (bundler.exe) to encode PNG/TGA -> XPR0 DXT1. The shared icon
comes from RXDK-VS20XX's extension icon by default. Paths are overridable:

  python scripts/gen-placeholder-images.py --icon <png> --bundler <bundler.exe>

Re-run only when the placeholder art or the set of samples-without-images changes; the produced
.xbx are committed, so a normal build does not need this.
"""
import os
import re
import sys
import glob
import struct
import shutil
import argparse
import subprocess
import tempfile
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "RxdkSamples"))
COMMON = os.path.join(ROOT, "Common")
TITLE_ASSET, SAVE_ASSET = "rxdk-titleimage.xbx", "rxdk-saveimage.xbx"

ap = argparse.ArgumentParser()
ap.add_argument("--icon", default=os.path.normpath(os.path.join(
    HERE, "..", "..", "RXDK-VS20XX", "RxdkVs.Package", "Resources", "extension-icon.png")))
ap.add_argument("--bundler", default=os.path.normpath(os.path.join(
    HERE, "..", "..", "RXDK-Tools", "src", "Rxdk.Bundler", "bin", "Release",
    "net8.0", "win-x64", "publish", "bundler.exe")))
args = ap.parse_args()

if not os.path.exists(args.bundler):
    sys.exit("bundler not found: %s (build Rxdk.Bundler or pass --bundler)" % args.bundler)


def png_to_xbx(src_img, size, out_xbx):
    """src_img: PIL image -> center-cropped to square -> size x size DXT1 XPR0 .xbx via bundler."""
    w, h = src_img.size
    side = min(w, h)  # center-crop to square so a non-square source isn't stretched
    img = src_img.crop(((w - side) // 2, (h - side) // 2, (w + side) // 2, (h + side) // 2))
    img = img.convert("RGBA").resize((size, size), Image.LANCZOS)
    with tempfile.TemporaryDirectory() as td:
        img.save(os.path.join(td, "src.tga"))
        with open(os.path.join(td, "i.rdf"), "w") as f:
            f.write("out_header i.h\nout_packedresource out.xpr\nout_error i.err\n"
                    "Texture img\n{\n Source src.tga\n Format D3DFMT_DXT1\n"
                    " Width %d\n Height %d\n Levels 1\n}\n" % (size, size))
        r = subprocess.run([args.bundler, "i.rdf"], cwd=td, capture_output=True, text=True)
        if r.returncode != 0:
            raise RuntimeError("bundler failed: " + (r.stderr or r.stdout))
        shutil.copyfile(os.path.join(td, "out.xpr"), out_xbx)


def rgb565(v):
    r, g, b = (v >> 11) & 0x1F, (v >> 5) & 0x3F, v & 0x1F
    return ((r * 255 + 15) // 31, (g * 255 + 31) // 63, (b * 255 + 15) // 31)


def decode_dxt1_xbx(path):
    b = open(path, "rb").read()
    hdr = struct.unpack_from("<I", b, 8)[0]
    fmt = struct.unpack_from("<I", b, 0x18)[0]
    w, h = 1 << ((fmt >> 20) & 0xF), 1 << ((fmt >> 24) & 0xF)
    data, img, px, off = b[hdr:], Image.new("RGBA", (w, h)), None, 0
    px = img.load()
    for by in range(0, h, 4):
        for bx in range(0, w, 4):
            c0, c1 = struct.unpack_from("<HH", data, off)
            bits = struct.unpack_from("<I", data, off + 4)[0]
            off += 8
            col = [rgb565(c0), rgb565(c1)]
            if c0 > c1:
                col += [tuple((2 * col[0][i] + col[1][i]) // 3 for i in range(3)),
                        tuple((col[0][i] + 2 * col[1][i]) // 3 for i in range(3))]
                alpha = [255, 255, 255, 255]
            else:
                col += [tuple((col[0][i] + col[1][i]) // 2 for i in range(3)), (0, 0, 0)]
                alpha = [255, 255, 255, 0]
            for i in range(16):
                idx = (bits >> (2 * i)) & 3
                x, y = bx + (i % 4), by + (i // 4)
                if x < w and y < h:
                    px[x, y] = col[idx] + (alpha[idx],)
    return img


# 1) shared placeholder assets from the RXDK icon
icon = Image.open(args.icon)
png_to_xbx(icon, 128, os.path.join(COMMON, TITLE_ASSET))
png_to_xbx(icon, 64, os.path.join(COMMON, SAVE_ASSET))
print("wrote Common/%s + Common/%s" % (TITLE_ASSET, SAVE_ASSET))


def find_img(d, kind):
    for f in os.listdir(d):
        if f.lower() == kind:
            return f
    return None


CFG_BLOCK = re.compile(
    r"(<PropertyGroup Condition=\"'\$\(Configuration\)\|\$\(Platform\)'=='(?:Debug|Release)\|Xbox'\">)(.*?)(</PropertyGroup>)",
    re.S)
STRIP = re.compile(r"[ \t]*<Rxdk(?:TitleImage|DefaultSaveImage)>[^<]*</Rxdk(?:TitleImage|DefaultSaveImage)>\r?\n")

derived = wired = 0
for vcx in glob.glob(os.path.join(ROOT, "**", "*.vcxproj"), recursive=True):
    d = os.path.dirname(vcx)
    title, save = find_img(d, "titleimage.xbx"), find_img(d, "saveimage.xbx")
    title_val = title or os.path.relpath(os.path.join(COMMON, TITLE_ASSET), d).replace("/", "\\")
    if save:
        save_val = save
    elif title:
        out = os.path.join(d, "saveimage.xbx")
        if not os.path.exists(out):
            png_to_xbx(decode_dxt1_xbx(os.path.join(d, title)), 64, out)
            derived += 1
        save_val = "saveimage.xbx"
    else:
        save_val = os.path.relpath(os.path.join(COMMON, SAVE_ASSET), d).replace("/", "\\")

    text = open(vcx, encoding="utf-8", newline="").read()

    def fix(m):
        body = STRIP.sub("", m.group(2)).rstrip()
        body += ("\r\n    <RxdkTitleImage>%s</RxdkTitleImage>"
                 "\r\n    <RxdkDefaultSaveImage>%s</RxdkDefaultSaveImage>\r\n  " % (title_val, save_val))
        return m.group(1) + body + m.group(3)

    new = CFG_BLOCK.sub(fix, text)
    if new != text:
        open(vcx, "w", encoding="utf-8", newline="").write(new)
        wired += 1

print("derived %d saves; wired %d vcxproj" % (derived, wired))
print("Now run: pwsh scripts/Generate-Manifests.ps1")
