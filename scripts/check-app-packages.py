#!/usr/bin/env python3
"""Check the release Apps package carries its compiled feature and dependency."""

import pathlib
import sys
import xml.etree.ElementTree as xml
import zipfile


def check(directory):
    packages = list(directory.glob("IntoChat.OS.Modules.Apps.[0-9]*.nupkg"))
    if not packages:
        raise ValueError(f"No IntoChat.OS.Modules.Apps package in {directory}")
    for package in packages:
        with zipfile.ZipFile(package) as archive:
            manifest = xml.fromstring(archive.read("IntoChat.OS.Modules.Apps.nuspec"))
            dependencies = {item.attrib["id"] for item in manifest.findall(".//{*}dependency")}
            errors = []
            assemblies = [name for name in archive.namelist() if name.startswith("lib/") and name.endswith("/IntoChat.OS.Modules.Apps.dll")]
            if not assemblies:
                errors.append("Apps assembly is missing")
            for assembly in assemblies:
                feature = assembly.rsplit("/", 1)[0] + "/IntoChat.Files.dll"
                if feature not in archive.namelist():
                    errors.append(f"compiled Files assembly is missing: {feature}")
            if "IntoChat.Files" in dependencies:
                errors.append("dependency on the unpublished IntoChat.Files package")
            if "DigitalBrain.Capabilities.Storage" not in dependencies:
                errors.append("compiled Files storage dependency is missing")
            if errors:
                raise ValueError(f"{package.name}: " + "; ".join(errors))
            print(f"{package.name}: compiled Files assembly and storage dependency verified")


if __name__ == "__main__":
    check(pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/packages"))
