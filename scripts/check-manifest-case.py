#!/usr/bin/env python3
"""Fail if any committed manifest references a path whose case doesn't match on disk.

The samples build on Windows (case-insensitive), but RXDK-VSCode also builds them on Linux/macOS
(case-sensitive). Every rxdk.project.json path reference -- sources, resources, deployPaths,
include paths, embed, projectReferences -- must therefore match the on-disk file/folder case
exactly. os.path.exists is case-INSENSITIVE on Windows, so this walks each path component against
os.listdir to compare real case (works the same on every OS).

Exit non-zero (listing offenders) if any reference is case-mismatched or missing. Run by CI.
"""
import os
import sys
import glob
import json

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "RxdkSamples"))


def exact_exists(abspath):
    """True only if every component of abspath matches on-disk case exactly."""
    abspath = os.path.normpath(abspath)
    drive, rest = os.path.splitdrive(abspath)
    cur = drive + os.sep
    for part in [p for p in rest.split(os.sep) if p]:
        try:
            entries = os.listdir(cur)
        except (FileNotFoundError, NotADirectoryError):
            return False
        if part not in entries:
            return False
        cur = os.path.join(cur, part)
    return True


def refs(body):
    out = []
    for k in ("sources", "resources", "deployPaths", "includePaths",
              "publicIncludePaths", "projectReferences"):
        out += [(k, v) for v in (body.get(k) or [])]
    for e in (body.get("embed") or []):
        if isinstance(e, dict) and e.get("path"):
            out.append(("embed", e["path"]))
    return out


case_bad = []
missing = []
seen = set()
for mf in glob.glob(os.path.join(ROOT, "**", "rxdk.project.json"), recursive=True):
    d = os.path.dirname(mf)
    m = json.load(open(mf, encoding="utf-8"))
    cfgs = m.get("configurations") or {"_": m}
    for body in cfgs.values():
        for kind, rel in refs(body):
            ap = os.path.normpath(os.path.join(d, rel.replace("\\", os.sep).replace("/", os.sep)))
            key = (os.path.relpath(mf, ROOT), kind, rel)
            if key in seen:
                continue
            seen.add(key)
            if exact_exists(ap):
                continue
            (case_bad if os.path.exists(ap) else missing).append(key)

for label, items in (("CASE MISMATCH (works on Windows, fails on Linux)", case_bad),
                     ("MISSING reference", missing)):
    if items:
        print("%s: %d" % (label, len(items)))
        for proj, kind, rel in sorted(items):
            print("  [%s] %s -> %s" % (kind, proj, rel))
        print()

if case_bad or missing:
    sys.exit("FAILED: %d case, %d missing. Fix the .vcxproj path case and run scripts/Generate-Manifests.ps1."
             % (len(case_bad), len(missing)))
print("OK: all manifest path references match on-disk case (%d checked)." % len(seen))
