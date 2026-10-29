#!/usr/bin/env python3
"""Check a proposed catalog against a previously published Git revision.

This is a *delta* check, complementary to the JSON/UnitsNet validator. It
detects identity reuse, mutated versions and unannounced breaking changes.
It does not assert compatibility with every historic Catverter executable.

Usage:
  python scripts/check_publication_compatibility.py --base-ref origin/main
"""

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path


SEMVER = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
PACK_PATH = re.compile(r"^packs/[a-z0-9-]+/[a-z0-9-]+\.json$")


def version(value, context, errors):
    if not isinstance(value, str) or not SEMVER.fullmatch(value):
        errors.append(f"{context} must use three numeric components (x.y.z)")
        return None
    return tuple(map(int, value.split(".")))


def catalog_packs(catalog, context, errors):
    entries = catalog.get("packs", [])
    if not isinstance(entries, list):
        errors.append(f"{context}.packs must be an array")
        return {}
    result = {}
    package_ids = set()
    for entry in entries:
        if not isinstance(entry, dict):
            errors.append(f"{context}.packs contains a non-object")
            continue
        pack_id, package_id = entry.get("id"), entry.get("packageId")
        if not isinstance(pack_id, str) or not isinstance(package_id, str):
            errors.append(f"{context} contains a pack without id/packageId")
            continue
        if pack_id in result:
            errors.append(f"{context} duplicates pack id {pack_id!r}")
        if package_id in package_ids:
            errors.append(f"{context} duplicates packageId {package_id!r}")
        result[pack_id] = entry
        package_ids.add(package_id)
    return result


def check_transition(previous, previous_docs, proposed, proposed_docs):
    """Return (blocking errors, advisory warnings) without I/O."""
    errors, warnings = [], []
    old = catalog_packs(previous, "published catalog", errors)
    new = catalog_packs(proposed, "proposed catalog", errors)
    if previous.get("repositoryId") != proposed.get("repositoryId"):
        errors.append("catalog repositoryId cannot be reassigned")
    if previous.get("schemaVersion") != proposed.get("schemaVersion"):
        errors.append("catalog schemaVersion needs a separate protocol migration")

    old_version = version(previous.get("version"), "published catalog version", errors)
    new_version = version(proposed.get("version"), "proposed catalog version", errors)
    if old_version and new_version:
        if new_version < old_version:
            errors.append("catalog version cannot decrease")
        if previous != proposed and new_version <= old_version:
            errors.append("catalog changed without increasing its version")

    old_min = version(
        previous.get("minimumAppVersion"), "published catalog minimumAppVersion", errors)
    new_min = version(
        proposed.get("minimumAppVersion"), "proposed catalog minimumAppVersion", errors)
    if old_min and new_min and new_min < old_min:
        errors.append("catalog minimumAppVersion cannot decrease")

    previous_pairs = {
        (conversion.get("quantity"), conversion.get("unit"))
        for doc in previous_docs.values()
        for conversion in doc.get("conversions", [])
    }
    for pack_id, reference in old.items():
        current = new.get(pack_id)
        if current is None:
            errors.append(
                f"pack {pack_id}: published pack removal needs a separate migration")
            continue
        if current.get("packageId") != reference.get("packageId"):
            errors.append(f"pack {pack_id}: packageId cannot be reassigned")
        if current.get("file") != reference.get("file"):
            errors.append(f"pack {pack_id}: file path cannot change in place")
        if current.get("classification") != reference.get("classification"):
            errors.append(f"pack {pack_id}: trust classification cannot change in place")

        old_pack = previous_docs.get(pack_id)
        new_pack = proposed_docs.get(pack_id)
        if old_pack is None or new_pack is None:
            errors.append(f"pack {pack_id}: missing pack data for comparison")
            continue

        for key in ("packageId", "id", "schemaVersion"):
            if old_pack.get(key) != new_pack.get(key):
                errors.append(f"pack {pack_id}: {key} changed; needs a new identity/schema")

        old_version = version(old_pack.get("version"), f"{pack_id} old version", errors)
        new_version = version(new_pack.get("version"), f"{pack_id} new version", errors)
        if old_version is None or new_version is None:
            continue
        if current.get("version") != new_pack.get("version"):
            errors.append(f"pack {pack_id}: catalog and file versions differ")
        if reference.get("version") != old_pack.get("version"):
            errors.append(f"pack {pack_id}: published catalog and file versions differ")
        if new_version < old_version:
            errors.append(f"pack {pack_id}: content version cannot decrease")
        if current.get("sha256") != reference.get("sha256") and new_version <= old_version:
            errors.append(
                f"pack {pack_id}: different bytes at an unchanged/non-increasing version")

        old_min = version(
            old_pack.get("minimumAppVersion"), f"{pack_id} old minimumAppVersion", errors)
        new_min = version(
            new_pack.get("minimumAppVersion"), f"{pack_id} new minimumAppVersion", errors)
        if old_min and new_min and new_min < old_min:
            errors.append(f"pack {pack_id}: minimumAppVersion cannot decrease")

        prior_conversions = {
            item["id"]: item for item in old_pack.get("conversions", [])
        }
        current_conversions = {
            item["id"]: item for item in new_pack.get("conversions", [])
        }
        old_modifiers = {item["id"] for item in old_pack.get("modifiers", [])}
        new_modifiers = {item["id"] for item in new_pack.get("modifiers", [])}

        removed_conversions = set(prior_conversions) - set(current_conversions)
        removed_modifiers = old_modifiers - new_modifiers
        added_conversions = set(current_conversions) - set(prior_conversions)
        added_modifiers = new_modifiers - old_modifiers

        for conversion_id in sorted(set(prior_conversions) & set(current_conversions)):
            before = prior_conversions[conversion_id]
            after = current_conversions[conversion_id]
            if (before.get("quantity"), before.get("unit")) != (
                after.get("quantity"), after.get("unit")
            ):
                errors.append(
                    f"pack {pack_id}/{conversion_id}: physical unit/quantity "
                    "changed under the same conversion id")

        if removed_conversions or removed_modifiers:
            if reference.get("classification") == "official":
                errors.append(
                    f"official pack {pack_id}: cannot remove previously bundled "
                    "conversion/modifier identities in place")
            elif new_version[0] <= old_version[0]:
                errors.append(
                    f"pack {pack_id}: removing conversions/modifiers requires "
                    "a major content-version bump")

        if (added_conversions or added_modifiers) and (
            new_version[0], new_version[1]
        ) <= (old_version[0], old_version[1]):
            errors.append(
                f"pack {pack_id}: adding conversions/modifiers requires a minor "
                "or major content-version bump")

        new_pairs = {
            (c.get("quantity"), c.get("unit"))
            for c in new_pack.get("conversions", [])
        } - previous_pairs
        if new_pairs and new_min == old_min:
            warnings.append(
                f"pack {pack_id}: new quantity/unit vocabulary {sorted(new_pairs)}; "
                "confirm minimumAppVersion against older app releases")
    for pack_id, current in new.items():
        if pack_id in old:
            continue
        for previous_pack in old.values():
            if current.get("packageId") == previous_pack.get("packageId"):
                errors.append(
                    f"pack {pack_id}: reuses an existing packageId")
        version(current.get("version"), f"new pack {pack_id} version", errors)
        doc = proposed_docs.get(pack_id)
        if doc is None:
            errors.append(f"new pack {pack_id}: missing file")
        elif doc.get("packageId") != current.get("packageId"):
            errors.append(f"new pack {pack_id}: catalog/file identity mismatch")

    return errors, warnings


def read_from_git(root, revision, path):
    output = subprocess.run(
        ["git", "-C", str(root), "show", f"{revision}:{path}"],
        check=True, capture_output=True, text=True, encoding="utf-8",
    )
    return json.loads(output.stdout)


def load_publication(root, revision=None):
    if revision is None:
        catalog = json.loads((root / "catalog.json").read_text(encoding="utf-8-sig"))
    else:
        catalog = read_from_git(root, revision, "catalog.json")

    docs = {}
    for reference in catalog.get("packs", []):
        path = reference["file"]
        if not PACK_PATH.fullmatch(path):
            raise ValueError(f"Unsafe pack path: {path!r}")
        if revision is None:
            document = json.loads((root / path).read_text(encoding="utf-8-sig"))
        else:
            document = read_from_git(root, revision, path)
        docs[reference["id"]] = document
    return catalog, docs


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-ref", required=True,
                        help="Published baseline commit or ref, e.g. origin/main")
    parser.add_argument("--root", type=Path, default=Path("."))
    args = parser.parse_args()
    root = args.root.resolve()
    try:
        previous, previous_docs = load_publication(root, args.base_ref)
        proposed, proposed_docs = load_publication(root)
        errors, warnings = check_transition(
            previous, previous_docs, proposed, proposed_docs)
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        print(f"Publication comparison could not run: {error}", file=sys.stderr)
        return 1

    for warning in warnings:
        print(f"REVIEW: {warning}")
    for error in errors:
        print(f"ERROR: {error}", file=sys.stderr)
    if errors:
        print(f"Publication compatibility failed ({len(errors)} issues).",
              file=sys.stderr)
        return 1

    print("Publication identities and content-version transitions validated.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
