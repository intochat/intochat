"""Record tested digests and promote those immutable artifacts across registries."""
import json
from pathlib import Path
import re
import subprocess
import sys

IMAGES = {"os", "os-ui", "os-sandbox"}


def validate(manifest, sha):
    if not re.fullmatch(r"[0-9a-f]{40}", sha) or manifest.get("commit") != sha:
        raise ValueError("Tested image manifest does not match the release commit.")
    images = manifest.get("images", {})
    if set(images) != IMAGES or any(not re.fullmatch(r"sha256:[0-9a-f]{64}", digest) for digest in images.values()):
        raise ValueError("Tested image manifest must contain all three immutable image digests.")


def az(*args):
    return subprocess.check_output(["az", *args], text=True).strip()


if __name__ == "__main__":
    action, sha, path = sys.argv[1:4]
    if action == "record":
        tag = subprocess.check_output(["git", "rev-parse", "--short", sha], text=True).strip()
        manifest = {"commit": sha, "images": {name: az("acr", "repository", "show", "-n", "acrintochattest",
                    "--image", f"{name}:{tag}", "--query", "digest", "-o", "tsv") for name in sorted(IMAGES)}}
        validate(manifest, sha)
        Path(path).write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    elif action in {"promote", "validate"}:
        manifest = json.loads(Path(path).read_text(encoding="utf-8"))
        validate(manifest, sha)
        if action == "validate":
            sys.exit(0)
        tag = sys.argv[4]
        if not re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?", tag):
            sys.exit("Production requires a versioned release tag.")
        source_id = az("acr", "show", "-n", "acrintochattest", "--query", "id", "-o", "tsv")
        for name, digest in sorted(manifest["images"].items()):
            existing = subprocess.run(["az", "acr", "repository", "show", "-n", "acrintochatprod", "--image",
                                       f"{name}:{tag}", "--query", "digest", "-o", "tsv"], capture_output=True, text=True)
            if existing.returncode == 0:
                if existing.stdout.strip() != digest:
                    sys.exit(f"Refusing to replace existing release image {name}:{tag} with a different digest.")
            else:
                az("acr", "import", "-n", "acrintochatprod", "--registry", source_id,
                   "--source", f"{name}@{digest}", "--image", f"{name}:{tag}")
            actual = az("acr", "repository", "show", "-n", "acrintochatprod", "--image", f"{name}:{tag}", "--query", "digest", "-o", "tsv")
            if actual != digest:
                sys.exit(f"Promoted digest mismatch for {name}.")
            print(f"Verified {name}:{tag} at {digest}.")
    else:
        sys.exit("Unknown manifest command.")
