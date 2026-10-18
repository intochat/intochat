#!/usr/bin/env python
"""Rename proof for the DigitalBrain -> IntoChat OS rename. Stdlib only; run as `python -I`.

  alias  <before> <after> --declared <tsv>   declared diff of ContractManifest, everything else byte-identical
  rename <before> <after> [--identity]       key-only rename: after == KEYMAP(before) for every JSON artifact
  paths  <repo>                              no two tracked paths differ only by case
  gate   <repo> --allowlist <file>           every brand hit is allow-listed

<before>/<after> are directories written by scripts/capture-all.ps1.
"""
import argparse
import json
import os
import re
import subprocess
import sys

BRAND = "Digital" + "Brain"  # split so this file never matches the gate it implements
MANIFEST = "ContractManifest.json"
RAW_FILES = ("Golden.cs",)  # compared byte for byte, never mapped

# Ordered key map (owner rulings: kernel host project is IntoChat.OS.Kernel).
KEY_MAP = [
    (re.compile(re.escape(BRAND) + r"\.slnx"), "Intochat.OS.slnx"),
    (re.compile(re.escape(BRAND) + r"\.OS(?=[./\\\"]|$)"), "IntoChat.OS.Kernel"),
    (re.compile(re.escape(BRAND) + r"_OS"), "IntoChat_OS_Kernel"),
    (re.compile(re.escape(BRAND) + r"_"), "IntoChat_OS_"),
    (re.compile(re.escape(BRAND) + r"\."), "IntoChat.OS."),
    (re.compile(r"(?<![A-Za-z0-9_])" + re.escape(BRAND) + r"(?![A-Za-z0-9_])"), "IntoChat.OS"),
    # identifier-embedded occurrences (IDigitalBrain, AddDigitalBrain...) follow the mechanical identifier rule
    (re.compile(re.escape(BRAND)), "IntoChatOS"),
]


def key_map(text):
    for pattern, replacement in KEY_MAP:
        text = pattern.sub(replacement, text)
    return text


def load_dir(directory):
    artifacts = {}
    for name in sorted(os.listdir(directory)):
        path = os.path.join(directory, name)
        if not os.path.isfile(path):
            continue
        with open(path, "rb") as handle:
            raw = handle.read()
        artifacts[name] = raw
    return artifacts


def parse(raw):
    return json.loads(raw.decode("utf-8"))


def read_declared(path):
    declared = set()
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.rstrip("\r\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            section, _, key = line.partition("\t")
            declared.add((section, key))
    return declared


def cmd_alias(args):
    before, after = load_dir(args.before), load_dir(args.after)
    failures = []
    if set(before) != set(after):
        failures.append("artifact sets differ: %s" % sorted(set(before) ^ set(after)))
    declared = read_declared(args.declared)
    actual = set()
    if MANIFEST in before and MANIFEST in after:
        old, new = parse(before[MANIFEST]), parse(after[MANIFEST])
        for section in sorted(set(old) | set(new)):
            old_section, new_section = old.get(section, {}), new.get(section, {})
            for key in set(old_section) | set(new_section):
                if old_section.get(key) != new_section.get(key):
                    actual.add((section, key))
    for name in sorted(set(before) & set(after)):
        if name == MANIFEST:
            continue
        if before[name] != after[name]:
            failures.append("%s is not byte-identical" % name)
    for item in sorted(declared - actual):
        failures.append("declared but unchanged: %s\t%s" % item)
    for item in sorted(actual - declared):
        failures.append("changed but not declared: %s\t%s" % item)
    print("manifest entries changed: %d, declared: %d" % (len(actual), len(declared)))
    return report(failures)


def map_tree(node):
    if isinstance(node, dict):
        return {key_map(key): map_tree(value) for key, value in node.items()}
    if isinstance(node, list):
        mapped = [map_tree(item) for item in node]
        if all(isinstance(item, str) for item in mapped):
            return sorted(mapped)  # path/package lists: position carries no meaning
        return mapped
    if isinstance(node, str):
        return key_map(node)
    return node


def normalise(node):
    if isinstance(node, dict):
        return {key: normalise(value) for key, value in node.items()}
    if isinstance(node, list):
        items = [normalise(item) for item in node]
        if all(isinstance(item, str) for item in items):
            return sorted(items)
        return items
    return node


def differences(expected, actual, path, found):
    if isinstance(expected, dict) and isinstance(actual, dict):
        for key in sorted(set(expected) | set(actual)):
            if key not in actual:
                found.append("%s: missing key %r" % (path, key))
            elif key not in expected:
                found.append("%s: unexpected key %r" % (path, key))
            else:
                differences(expected[key], actual[key], "%s/%s" % (path, key), found)
    elif isinstance(expected, list) and isinstance(actual, list):
        if len(expected) != len(actual):
            found.append("%s: list length %d != %d" % (path, len(expected), len(actual)))
        else:
            for index, (left, right) in enumerate(zip(expected, actual)):
                differences(left, right, "%s[%d]" % (path, index), found)
    elif expected != actual:
        found.append("%s: expected %r, found %r" % (path, str(expected)[:120], str(actual)[:120]))


def cmd_rename(args):
    if args.identity:  # self-test: compare a snapshot with itself, mapping nothing
        global KEY_MAP
        KEY_MAP = []
    before, after = load_dir(args.before), load_dir(args.after)
    failures = []
    if set(before) != set(after):
        failures.append("artifact sets differ: %s" % sorted(set(before) ^ set(after)))
    for name in sorted(set(before) & set(after)):
        if name in RAW_FILES or not name.endswith(".json"):
            if before[name] != after[name]:
                failures.append("%s is not byte-identical" % name)
            continue
        expected = map_tree(parse(before[name]))
        actual = normalise(parse(after[name]))
        found = []
        differences(expected, actual, name, found)
        failures.extend(found[:25])
        if len(found) > 25:
            failures.append("%s: ... %d more differences" % (name, len(found) - 25))
    return report(failures)


def tracked(repo):
    output = subprocess.run(["git", "-C", repo, "ls-files", "-z"], capture_output=True, check=True).stdout
    return [path for path in output.decode("utf-8").split("\0") if path]


def cmd_paths(args):
    seen, failures = {}, []
    for path in tracked(args.repo):
        seen.setdefault(path.lower(), []).append(path)
    for lowered, paths in sorted(seen.items()):
        if len(paths) > 1:
            failures.append("case collision: %s" % " | ".join(paths))
    print("tracked paths: %d" % sum(len(paths) for paths in seen.values()))
    return report(failures)


def glob_regex(glob):
    out, index = "", 0
    while index < len(glob):
        if glob.startswith("**/", index):
            out += "(?:.*/)?"
            index += 3
        elif glob.startswith("**", index):
            out += ".*"
            index += 2
        elif glob[index] == "*":
            out += "[^/]*"
            index += 1
        elif glob[index] == "?":
            out += "[^/]"
            index += 1
        else:
            out += re.escape(glob[index])
            index += 1
    return re.compile("^" + out + "$")


def read_rules(path):
    rules = []
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.rstrip("\r\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            glob, _, regex = line.partition("\t")
            rules.append((glob, glob_regex(glob), regex, re.compile(regex)))
    return rules


def cmd_gate(args):
    rules = read_rules(args.allowlist)
    used = [0] * len(rules)
    result = subprocess.run(
        ["git", "-C", args.repo, "grep", "-nIiE", BRAND.lower(), "--", ".", ":!CHANGELOG*", ":!scripts/rename/**"],
        capture_output=True,
    )
    if result.returncode not in (0, 1):
        print(result.stderr.decode("utf-8", "replace"), file=sys.stderr)
        return 2
    unmatched, files, total = [], set(), 0
    for line in result.stdout.decode("utf-8", "replace").splitlines():
        path, _, rest = line.partition(":")
        _, _, text = rest.partition(":")
        total += 1
        files.add(path)
        for index, (_, path_regex, _, line_regex) in enumerate(rules):
            if path_regex.match(path) and line_regex.search(text):
                used[index] += 1
                break
        else:
            unmatched.append(line[:200])
    print("gate hits: %d lines in %d files; unmatched: %d" % (total, len(files), len(unmatched)))
    for index, (glob, _, regex, _) in enumerate(rules):
        if used[index] == 0:
            print("INFO unused allow-list rule: %s\t%s" % (glob, regex))
    return report(unmatched[:200])


def report(failures):
    for failure in failures:
        print("FAIL: " + failure)
    if failures:
        return 1
    print("OK")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    alias = sub.add_parser("alias")
    alias.add_argument("before")
    alias.add_argument("after")
    alias.add_argument("--declared", required=True)
    alias.set_defaults(run=cmd_alias)
    rename = sub.add_parser("rename")
    rename.add_argument("before")
    rename.add_argument("after")
    rename.add_argument("--identity", action="store_true", help="map nothing (self-test)")
    rename.set_defaults(run=cmd_rename)
    paths = sub.add_parser("paths")
    paths.add_argument("repo")
    paths.set_defaults(run=cmd_paths)
    gate = sub.add_parser("gate")
    gate.add_argument("repo")
    gate.add_argument("--allowlist", required=True)
    gate.set_defaults(run=cmd_gate)
    args = parser.parse_args()
    return args.run(args)


if __name__ == "__main__":
    sys.exit(main())
