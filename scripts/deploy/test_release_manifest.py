import unittest
from release_manifest import validate


class ReleaseManifestFacts(unittest.TestCase):
    def test_rejects_wrong_commit_missing_image_and_invalid_digest(self):
        sha = "a" * 40
        manifest = {"commit": sha, "images": {name: "sha256:" + "b" * 64 for name in
                    ["os", "os-ui", "os-sandbox"]}}
        validate(manifest, sha)
        with self.assertRaises(ValueError):
            validate(manifest, "c" * 40)
        for name in manifest["images"]:
            with self.subTest(image=name), self.assertRaises(ValueError):
                validate({**manifest, "images": {key: value for key, value in manifest["images"].items() if key != name}}, sha)
        with self.assertRaises(ValueError):
            validate({**manifest, "images": {**manifest["images"], "os-ui": "latest"}}, sha)
