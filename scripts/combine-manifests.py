#!/usr/bin/env python3
"""Combine per-configuration RXDK manifests into the committed multi-config file.

For every sample .vcxproj under RxdkSamples/, reads the Debug/Release single-config
JSONs that genmanifests.proj wrote to the project's out/ folder and merges them into a
committed multi-config rxdk.project.json:

    { "name": <shared>, "defaultConfiguration": "Debug",
      "configurations": { "Debug": {...}, "Release": {...} } }

The output is deterministic (json.dump indent=2, LF newlines), so re-running with an
unchanged .vcxproj reproduces byte-identical files -- which is what the -Check drift
gate in Generate-Manifests.ps1 (and CI) relies on.
"""
import os
import sys
import glob
import json

# Repo root = parent of this scripts/ dir; samples live under RxdkSamples/.
ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "RxdkSamples")
ROOT = os.path.normpath(ROOT)

made = 0
skipped = []
for vcx in glob.glob(os.path.join(ROOT, "**", "*.vcxproj"), recursive=True):
    d = os.path.dirname(vcx)
    dbg = os.path.join(d, "out", "rxdk.Debug.json")
    rel = os.path.join(d, "out", "rxdk.Release.json")
    if not (os.path.exists(dbg) and os.path.exists(rel)):
        skipped.append(d)
        continue
    with open(dbg, encoding="utf-8") as f:
        D = json.load(f)
    with open(rel, encoding="utf-8") as f:
        R = json.load(f)
    name = D.get("name") or os.path.splitext(os.path.basename(vcx))[0]
    # name is shared at the top level; drop the redundant per-config copies.
    for body in (D, R):
        body.pop("name", None)
    manifest = {
        "name": name,
        "defaultConfiguration": "Debug",
        "configurations": {"Debug": D, "Release": R},
    }
    with open(os.path.join(d, "rxdk.project.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    made += 1

print("wrote %d rxdk.project.json ; skipped %d" % (made, len(skipped)))
for s in skipped[:10]:
    print("  skipped (no per-config JSON):", s)

# Fail loudly if nothing was produced -- almost always means the Xbox platform (and its
# RxdkGenerateManifest target) isn't installed, so the MSBuild step emitted nothing.
if made == 0:
    sys.exit("ERROR: no manifests were combined -- did the MSBuild generate step run?")
