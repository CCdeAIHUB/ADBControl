#!/usr/bin/env python3
import argparse
import hashlib
import json
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as file:
        for chunk in iter(lambda: file.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify ADBControl ADB manifest assets.")
    parser.add_argument("--manifest", default="assets/adb/manifest.json")
    parser.add_argument("--allow-missing-custom", action="store_true")
    args = parser.parse_args()

    repo_root = Path.cwd()
    manifest_path = repo_root / args.manifest
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    failures: list[str] = []

    for asset in manifest.get("assets", []):
        target_id = asset["targetId"]
        source = asset["source"]
        path = repo_root / asset["path"]
        expected_hash = asset.get("sha256")

        if not path.exists():
            if args.allow_missing_custom and source == "custom-github-build":
                print(f"SKIP custom asset not present yet: {target_id} -> {asset['path']}")
                continue
            failures.append(f"Missing asset for {target_id}: {asset['path']}")
            continue

        if expected_hash:
            actual_hash = sha256(path)
            if actual_hash.lower() != expected_hash.lower():
                failures.append(
                    f"SHA256 mismatch for {target_id}: expected {expected_hash}, got {actual_hash}"
                )
            else:
                print(f"OK {target_id}: {actual_hash}")
        else:
            print(f"OK {target_id}: present, sha256 not pinned")

    if failures:
        print("\nADB manifest verification failed:")
        for failure in failures:
            print(f"- {failure}")
        return 1

    print("ADB manifest verification passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
