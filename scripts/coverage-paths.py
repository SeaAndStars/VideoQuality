#!/usr/bin/env python3
"""Merges a coverlet JSON report from another checkout (another OS) into this one's.

    coverage-paths.py <their.json> <this repo root> <merged.json>

File paths are rewritten from the other checkout's root to this one (Windows separators become
'/'), then hits are added to <merged.json> line by line and branch by branch, so a branch taken
only on Windows and its other side taken only on Linux both count.
"""
import json
import sys

theirs_path, root, merged_path = sys.argv[1], sys.argv[2].rstrip("/"), sys.argv[3]
theirs = json.load(open(theirs_path, encoding="utf-8-sig"))
merged = json.load(open(merged_path, encoding="utf-8-sig"))


def rebase(path):
    unix = path.replace("\\", "/")
    for marker in ("/VideoQuality.Core/", "/VideoQuality.App/", "/VideoQuality.Cli/"):
        if marker in unix:
            return root + marker + unix.split(marker, 1)[1]
    return unix


def merge_method(into, other):
    for line, hits in other.get("Lines", {}).items():
        into.setdefault("Lines", {})[line] = into.get("Lines", {}).get(line, 0) + hits
    branches = {(b["Line"], b["Offset"], b["EndOffset"], b["Path"], b["Ordinal"]): b for b in into.setdefault("Branches", [])}
    for b in other.get("Branches", []):
        key = (b["Line"], b["Offset"], b["EndOffset"], b["Path"], b["Ordinal"])
        if key in branches:
            branches[key]["Hits"] += b["Hits"]
        else:
            into["Branches"].append(dict(b))


for module, files in theirs.items():
    target = merged.setdefault(module, {})
    for path, classes in files.items():
        file_target = target.setdefault(rebase(path), {})
        for cls, methods in classes.items():
            class_target = file_target.setdefault(cls, {})
            for method, data in methods.items():
                merge_method(class_target.setdefault(method, {"Lines": {}, "Branches": []}), data)

json.dump(merged, open(merged_path, "w", encoding="utf-8"))
