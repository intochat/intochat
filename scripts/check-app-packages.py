#!/usr/bin/env python3
"""Check the prebuilt built-in app packages: python3 scripts/check-app-packages.py [compiled-apps/os] [features/os].

Every features/os app folder (one with an app.cs) must have <name>.intochat-app, and each package must hold what the
format promises: canonical app.json, a payload inventory that matches the archive entry for entry (length and sha256),
a managed entry assembly, one assembly per synapse source, no PDBs or .cs files, and the app's own sources and assets
so the app stays editable. Deep validation (reader, determinism, references) is the BuiltinPackageFacts test suite.
"""

import hashlib
import json
import pathlib
import re
import sys
import zipfile


def declared_name(app_cs):
    match = re.search(r'const\s+string\s+Name\s*=\s*"([^"]+)"', app_cs.read_text(encoding="utf-8"))
    if not match:
        raise ValueError(f"{app_cs}: no App.Name")
    return match.group(1)


def check_package(path, folder):
    errors = []
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(set(n.lower() for n in names)) != len(names):
            errors.append("case-colliding or duplicate entries")
        if "app.json" not in names:
            return ["app.json is missing"]
        manifest = json.loads(archive.read("app.json"))
        if (manifest.get("schemaVersion"), manifest.get("hostAbi"), manifest.get("targetFramework")) != (1, 1, "net11.0"):
            errors.append("unsupported schema, host ABI or target framework")
        inventory = {item["path"]: item for item in manifest.get("payload", [])}
        if sorted(inventory) != sorted(n for n in names if n != "app.json"):
            errors.append("payload inventory does not match the archive entries")
        for name, item in inventory.items():
            if name in archive.namelist():
                data = archive.read(name)
                if len(data) != item["length"] or hashlib.sha256(data).hexdigest() != item["sha256"]:
                    errors.append(f"{name}: length or sha256 differs from the inventory")
                if name.endswith(".dll") and data[:2] != b"MZ":
                    errors.append(f"{name}: not a managed assembly")
            if name.endswith((".pdb", ".cs")):
                errors.append(f"{name}: PDBs and .cs files are not payload")
        if manifest.get("entryAssembly") not in inventory:
            errors.append("entry assembly is not in the inventory")
        if manifest.get("sharedAssemblies"):
            errors.append("a built-in carries no private copy of shared assemblies")
        # every program source of the folder has its packaged copy; every synapse has its assembly
        for source in folder.rglob("*"):
            relative = source.relative_to(folder).as_posix()
            if source.is_file() and not relative.startswith(("compiled/", "hosting/", "tests/", "bin/", "obj/")):
                packaged = "files/" + relative + (".src" if relative.endswith(".cs") else "")
                if packaged not in inventory:
                    errors.append(f"{relative}: not carried as {packaged}")
                elif hashlib.sha256(source.read_bytes().replace(b"\r\n", b"\n")).hexdigest() != inventory[packaged]["sha256"]:
                    errors.append(f"{relative}: packaged copy differs from the source")
        synapses = json.loads(manifest.get("hostMetadata", {}).get("synapses", "[]"))
        if not synapses:
            errors.append("no synapse assemblies")
        for synapse in synapses:
            if synapse["assembly"] not in inventory or synapse["source"] not in inventory:
                errors.append(f"synapse {synapse['source']} lacks its assembly or source")
        if len(synapses) != len([n for n in inventory if n.startswith("assemblies/")]):
            errors.append("assemblies do not match the declared synapses")
        icon = manifest.get("presentation", {}).get("icon")
        if icon and icon not in inventory:
            errors.append(f"icon {icon} is not in the package")
    return errors


def check(packages, features):
    apps = {}
    for app_cs in sorted(features.glob("*/app.cs")):
        apps[declared_name(app_cs)] = app_cs.parent
    if not apps:
        raise ValueError(f"No built-in apps under {features}")
    failures = []
    for name, folder in apps.items():
        path = packages / f"{name}.intochat-app"
        if not path.exists():
            failures.append(f"{name}: {path} is missing")
            continue
        errors = check_package(path, folder)
        if errors:
            failures.append(f"{name}: " + "; ".join(errors))
        else:
            print(f"{name}: {path.stat().st_size} bytes, {hashlib.sha256(path.read_bytes()).hexdigest()}")
    extra = sorted(p.name for p in packages.glob("*.intochat-app") if p.stem not in apps)
    if extra:
        failures.append("packages without a built-in app: " + ", ".join(extra))
    if failures:
        raise ValueError("\n".join(failures))
    print(f"{len(apps)} built-in app packages verified")


if __name__ == "__main__":
    try:
        check(pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "compiled-apps/os"),
              pathlib.Path(sys.argv[2] if len(sys.argv) > 2 else "features/os"))
    except ValueError as error:
        print(error, file=sys.stderr)
        sys.exit(1)
