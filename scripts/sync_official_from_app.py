#!/usr/bin/env python3
"""Synchronize canonical official packs/locales from a committed CatverterV2 checkout.

This keeps the public pack catalogue in step with the private application's
bundled source. Contributor-owned packs are deliberately left untouched.

Usage:
  python scripts/sync_official_from_app.py --app ../CatverterV2
  python scripts/sync_official_from_app.py --app ../CatverterV2 --verify
"""

import argparse
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path


SOURCE_REPO = "https://github.com/namireki/CatverterV2"
APP_REPOSITORY_ID = "com.namirekistudio.catverter.packs"


def normalized_bytes(text: str) -> bytes:
    return text.replace("\r\n", "\n").replace("\r", "\n").encode("utf-8")


def digest(text: str) -> str:
    return hashlib.sha256(normalized_bytes(text)).hexdigest()


def read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")


def write_text(path: Path, text: str, verify: bool) -> None:
    text = text.rstrip("\n") + "\n"
    if verify:
        if not path.is_file() or read_text(path) != text:
            raise ValueError(f"Not synchronized: {path}")
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(normalized_bytes(text))


def repository_revision(path: Path) -> str:
    result = subprocess.run(
        ["git", "-C", str(path), "rev-parse", "HEAD"],
        check=True,
        capture_output=True,
        text=True,
    )
    return result.stdout.strip()


def ensure_no_dirty_files(path: Path) -> None:
    result = subprocess.run(
        ["git", "-C", str(path), "status", "--porcelain", "--", "packs/"],
        check=True,
        capture_output=True,
        text=True,
    )
    if result.stdout.strip():
        raise ValueError("Commit the CatverterV2 packs/ changes before synchronizing.")


def read_json(path: Path) -> dict:
    return json.loads(read_text(path))


def validate_locale(locale: dict, official: dict[str, dict]) -> None:
    for pack_id, pack in official.items():
        localized_pack = locale.get("packs", {}).get(pack_id)
        if localized_pack is None or not localized_pack.get("name") or not localized_pack.get("description"):
            raise ValueError(f"{locale['locale']} missing pack {pack_id} name/description")
        expected_ids = {entry["id"] for entry in pack["conversions"]}
        actual_ids = set(localized_pack.get("conversions", {}))
        if actual_ids != expected_ids:
            raise ValueError(
                f"{locale['locale']} / {pack_id}: missing {len(expected_ids - actual_ids)} "
                f"and stale {len(actual_ids - expected_ids)} conversion IDs; "
                "complete localization before publishing"
            )
        for conversion_id, forms in localized_pack["conversions"].items():
            if not forms.get("one") or not forms.get("other"):
                raise ValueError(f"{locale['locale']} missing plural forms for {pack_id}/{conversion_id}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", required=True, type=Path, help="Checked-out CatverterV2 repository")
    parser.add_argument("--verify", action="store_true", help="Compare only, without modifying files")
    parser.add_argument("--catalog-version", help="New canonical catalogue version (defaults to next minor)")
    args = parser.parse_args()

    app = args.app.resolve()
    root = Path(__file__).resolve().parent.parent
    ensure_no_dirty_files(app)
    current_revision = repository_revision(app)
    if not re.fullmatch(r"[a-f0-9]{40}", current_revision):
        raise ValueError("CatverterV2 checkout does not identify a Git commit")

    bundled_catalog = read_json(app / "packs" / "catalog.json")
    canonical_catalog = read_json(root / "catalog.json")
    # Verification compares bytes/identities against the published source
    # revision, even if the app subsequently merged documentation commits.
    # Actual synchronization always records the current committed revision.
    revision = (canonical_catalog["sourceCatalog"]["revision"]
                if args.verify else current_revision)
    official = {entry["id"]: entry for entry in canonical_catalog["packs"]
                if entry.get("classification") == "official"}
    bundled = {entry["id"]: entry for entry in bundled_catalog["packs"]}
    if set(official) != set(bundled):
        raise ValueError(
            f"Official pack sets differ: online={sorted(official)} app={sorted(bundled)}"
        )

    source_packs: dict[str, dict] = {}
    files: list[tuple[Path, str]] = []
    for pack_id, entry in official.items():
        original = bundled[pack_id]
        source_file = app / "packs" / original["file"]
        app_text = read_text(source_file)
        pack = json.loads(app_text)
        if (pack["id"] != pack_id or pack["packageId"] != entry["packageId"]
                or pack["version"] != original["version"]
                or digest(app_text) != original["sha256"]):
            raise ValueError(f"Bundled catalogue identity/hash mismatch: {pack_id}")

        expected_source = "../schemas/catverter-pack-v1.schema.json"
        if pack["$schema"] != expected_source:
            raise ValueError(f"Unexpected bundled schema path: {pack_id}")
        online_text = app_text.replace(
            '"$schema": "' + expected_source + '"',
            '"$schema": "../../schemas/catverter-pack-v1.schema.json"',
            1,
        )
        online_file = Path("packs") / original["file"]
        if entry["file"] != online_file.as_posix():
            raise ValueError(f"Unexpected canonical path: {pack_id}")

        entry["version"] = pack["version"]
        entry["sha256"] = digest(online_text)
        entry["source"] = {
            "repository": SOURCE_REPO,
            "revision": revision,
            "file": "packs/" + original["file"],
            "sha256": digest(app_text),
        }
        source_packs[pack_id] = pack
        files.append((root / online_file, online_text))

    for locale_ref in canonical_catalog["locales"]:
        locale_id = locale_ref["locale"]
        source = app / "packs" / "locales" / f"{locale_id}.json"
        if not source.is_file():
            raise ValueError(f"Missing editorial locale source: {source}")
        text = read_text(source)
        locale = json.loads(text)
        if locale["locale"] != locale_id:
            raise ValueError(f"Locale identity mismatch: {source}")
        validate_locale(locale, source_packs)
        locale_ref["sha256"] = digest(text)
        locale_ref["source"] = {
            "repository": SOURCE_REPO,
            "revision": revision,
            "file": f"packs/locales/{locale_id}.json",
            "sha256": digest(text),
        }
        files.append((root / locale_ref["file"], text))

    source_catalog = canonical_catalog["sourceCatalog"]
    source_catalog.update({
        "repository": SOURCE_REPO,
        "revision": revision,
        "file": "packs/catalog.json",
        "repositoryId": APP_REPOSITORY_ID,
        "version": bundled_catalog["version"],
    })
    if not args.verify:
        version = args.catalog_version
        if version is None:
            major, minor, _ = map(int, canonical_catalog["version"].split("."))
            version = f"{major}.{minor + 1}.0"
        if not re.fullmatch(r"\d+\.\d+\.\d+", version):
            raise ValueError("Provide a dotted x.y.z catalogue version")
        canonical_catalog["version"] = version

    for path, text in files:
        write_text(path, text, verify=args.verify)

    # Keep the previous canonical version in --verify mode; it was deliberately
    # bumped during the original sync. All other provenance must match.
    if args.verify:
        current_catalog = read_json(root / "catalog.json")
        canonical_catalog["version"] = current_catalog["version"]

    catalog_text = json.dumps(canonical_catalog, ensure_ascii=False, indent=2) + "\n"
    write_text(root / "catalog.json", catalog_text, verify=args.verify)
    print(f"{'Verified' if args.verify else 'Synchronized'} {len(official)} official packs, "
          f"{len(canonical_catalog['locales'])} locales, source {revision[:12]}")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
