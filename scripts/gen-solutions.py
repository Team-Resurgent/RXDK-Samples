#!/usr/bin/env python3
"""Generate a per-sample Visual Studio 2022 solution (.sln) next to each sample .vcxproj.

Each sample is a single .vcxproj; this writes a matching <ProjectName>.sln so the sample can be
opened directly in VS 2022 (double-click) with its solution config/startup state persisted, instead
of VS synthesizing a throwaway solution. Output is deterministic (project GUID + name + the
project's own configuration/platform list), so re-running with an unchanged .vcxproj reproduces a
byte-identical .sln -- which the -Check drift gate relies on.
"""
import os
import re
import sys
import glob

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "RxdkSamples"))

VCPROJ_TYPE_GUID = "{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}"  # Visual C++ project

GUID_RE = re.compile(r"<ProjectGuid>\s*(\{[0-9A-Fa-f\-]+\})\s*</ProjectGuid>")
NAME_RE = re.compile(r"<ProjectName>\s*([^<]+?)\s*</ProjectName>")
CFG_RE = re.compile(r'<ProjectConfiguration\s+Include="([^"|]+)\|([^"]+)"')


def read(path):
    with open(path, encoding="utf-8", errors="ignore") as f:
        return f.read()


def build_sln(vcxproj_name, proj_name, guid, configs):
    """configs: ordered list of (Configuration, Platform) tuples."""
    L = []
    L.append("")  # VS writes a blank first line after the BOM
    L.append("Microsoft Visual Studio Solution File, Format Version 12.00")
    L.append("# Visual Studio Version 17")
    L.append("VisualStudioVersion = 17.0.31903.59")
    L.append("MinimumVisualStudioVersion = 10.0.40219.1")
    L.append('Project("%s") = "%s", "%s", "%s"'
             % (VCPROJ_TYPE_GUID, proj_name, vcxproj_name, guid))
    L.append("EndProject")
    L.append("Global")
    L.append("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution")
    for cfg, plat in configs:
        L.append("\t\t%s|%s = %s|%s" % (cfg, plat, cfg, plat))
    L.append("\tEndGlobalSection")
    L.append("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution")
    for cfg, plat in configs:
        L.append("\t\t%s.%s|%s.ActiveCfg = %s|%s" % (guid, cfg, plat, cfg, plat))
        L.append("\t\t%s.%s|%s.Build.0 = %s|%s" % (guid, cfg, plat, cfg, plat))
    L.append("\tEndGlobalSection")
    L.append("\tGlobalSection(SolutionProperties) = preSolution")
    L.append("\t\tHideSolutionNode = FALSE")
    L.append("\tEndGlobalSection")
    L.append("EndGlobal")
    # VS solutions are CRLF with a UTF-8 BOM.
    return "﻿" + "\r\n".join(L) + "\r\n"


made = 0
problems = []
for vcx in glob.glob(os.path.join(ROOT, "**", "*.vcxproj"), recursive=True):
    text = read(vcx)
    gm = GUID_RE.search(text)
    if not gm:
        problems.append("no ProjectGuid: " + os.path.relpath(vcx, ROOT))
        continue
    guid = gm.group(1).upper()
    nm = NAME_RE.search(text)
    proj_name = nm.group(1).strip() if nm else os.path.splitext(os.path.basename(vcx))[0]
    configs = []
    for cfg, plat in CFG_RE.findall(text):
        if (cfg, plat) not in configs:
            configs.append((cfg, plat))
    if not configs:
        configs = [("Debug", "Xbox"), ("Release", "Xbox")]
    sln = build_sln(os.path.basename(vcx), proj_name, guid, configs)
    dest = os.path.join(os.path.dirname(vcx), proj_name + ".sln")
    with open(dest, "w", encoding="utf-8", newline="") as f:
        f.write(sln)
    made += 1

print("wrote %d .sln" % made)
for p in problems:
    print("  WARN:", p)
if problems:
    sys.exit("Some projects had no ProjectGuid.")
