#!/usr/bin/env python3
"""Download only the pinned, publisher-checksummed build archives."""
import base64
import hashlib
import json
from pathlib import Path
import urllib.request

ROOT = Path(__file__).resolve().parent
ARCHIVES = ROOT / "dependencies" / "archives"


def valid(path, dependency):
    if not path.is_file():
        return False
    algorithm = "sha256" if "sha256" in dependency else "sha512"
    digest = hashlib.new(algorithm)
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    if algorithm == "sha256":
        return digest.hexdigest() == dependency["sha256"]
    return "sha512-" + base64.b64encode(digest.digest()).decode() == dependency["integrity"]


def main():
    ARCHIVES.mkdir(parents=True, exist_ok=True)
    manifest = json.loads((ROOT / "OfflineDependencies.json").read_text())
    for dependency in manifest["dependencies"]:
        target = ARCHIVES / dependency["archive"]
        if valid(target, dependency):
            print(f"Already verified: {target.name}")
            continue
        temporary = target.with_suffix(target.suffix + ".download")
        try:
            print(f"Downloading: {dependency['name']} {dependency['version']}")
            request = urllib.request.Request(dependency["url"], headers={"User-Agent": "OpenCodeTelegram-build"})
            with urllib.request.urlopen(request, timeout=180) as response, temporary.open("wb") as output:
                for block in iter(lambda: response.read(1024 * 1024), b""):
                    output.write(block)
            if not valid(temporary, dependency):
                raise RuntimeError(f"Checksum mismatch: {target.name}")
            temporary.replace(target)
        finally:
            temporary.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
