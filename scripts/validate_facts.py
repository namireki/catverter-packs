#!/usr/bin/env python3
import argparse
import hashlib
import json
import re
from pathlib import Path

ID = re.compile(r"^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$")
VERSION = re.compile(r"^[0-9]+(?:\.[0-9]+){1,3}$")
LOCALE = re.compile(r"^[a-z]{2}(?:-[A-Za-z0-9]+)*$")
PACK_PATH = re.compile(
    r"^packs/[a-z0-9](?:[a-z0-9-]*[a-z0-9])?/"
    r"[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.json$")
KINDS = {"fact", "comparison", "absurd-comparison"}


def fail(message):
    raise SystemExit(f"cat facts validation failed: {message}")


def load_json(path):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        fail(f"{path}: {exc}")


def require_version(value, label):
    if not isinstance(value, str) or not VERSION.fullmatch(value):
        fail(f"{label} must be a dotted numeric version")


def normalized_sha256(path):
    text = path.read_text(encoding="utf-8")
    normalized = text.replace("\r\n", "\n").replace("\r", "\n")
    return hashlib.sha256(normalized.encode("utf-8")).hexdigest()


def validate(root):
    catalog_path = root / "facts" / "catalog.json"
    catalog = load_json(catalog_path)
    if catalog.get("documentType") != "catverter-fact-catalog":
        fail("facts/catalog.json has the wrong documentType")
    if catalog.get("schemaVersion") != 1:
        fail("facts/catalog.json must use schemaVersion 1")
    require_version(catalog.get("version"), "fact catalog version")
    require_version(
        catalog.get("minimumAppVersion"),
        "fact catalog minimumAppVersion")
    if catalog.get("file") != "facts/cat-facts.json":
        fail("fact catalog file must be facts/cat-facts.json")
    if catalog.get("contentBaseUrl") != (
        "https://raw.githubusercontent.com/namireki/"
        "catverter-packs/{revision}/"
    ):
        fail("fact catalog contentBaseUrl is not the canonical immutable base")

    facts_path = root / catalog["file"]
    expected_hash = catalog.get("sha256")
    actual_hash = normalized_sha256(facts_path)
    if expected_hash != actual_hash:
        fail(
            "fact document SHA-256 mismatch: "
            f"expected {expected_hash}, got {actual_hash}"
        )

    document = load_json(facts_path)
    if document.get("documentType") != "catverter-facts":
        fail("facts/cat-facts.json has the wrong documentType")
    if document.get("schemaVersion") != 1:
        fail("facts/cat-facts.json must use schemaVersion 1")
    require_version(document.get("version"), "facts version")
    require_version(
        document.get("minimumAppVersion"),
        "facts minimumAppVersion")

    facts = document.get("facts")
    if not isinstance(facts, list) or not facts:
        fail("facts must be a non-empty array")

    pack_cache = {}
    ids = set()
    for index, fact in enumerate(facts):
        label = f"facts[{index}]"
        if not isinstance(fact, dict):
            fail(f"{label} must be an object")
        fact_id = fact.get("id")
        if not isinstance(fact_id, str) or not ID.fullmatch(fact_id):
            fail(f"{label}.id is invalid")
        if fact_id in ids:
            fail(f"duplicate fact id: {fact_id}")
        ids.add(fact_id)

        if fact.get("kind") not in KINDS:
            fail(f"{fact_id}: invalid kind")
        category = fact.get("category")
        if not isinstance(category, str) or not ID.fullmatch(category):
            fail(f"{fact_id}: invalid category")

        texts = fact.get("text")
        if not isinstance(texts, dict) or not texts:
            fail(f"{fact_id}: text must be a non-empty object")
        if not isinstance(texts.get("en"), str) or not texts["en"].strip():
            fail(f"{fact_id}: an English text is required")
        for locale, text in texts.items():
            if not LOCALE.fullmatch(locale):
                fail(f"{fact_id}: invalid locale {locale!r}")
            if not isinstance(text, str) or not text.strip():
                fail(f"{fact_id}: empty text for locale {locale}")

        references = fact.get("references", [])
        if not isinstance(references, list):
            fail(f"{fact_id}: references must be an array")
        for reference in references:
            if not isinstance(reference, dict):
                fail(f"{fact_id}: each reference must be an object")
            file_name = reference.get("file")
            conversion_id = reference.get("conversionId")
            if not isinstance(file_name, str) or not PACK_PATH.fullmatch(file_name):
                fail(f"{fact_id}: invalid reference file")
            if not isinstance(conversion_id, str) or not ID.fullmatch(conversion_id):
                fail(f"{fact_id}: invalid reference conversionId")

            pack_path = root / file_name
            if file_name not in pack_cache:
                pack = load_json(pack_path)
                conversions = pack.get("conversions", [])
                pack_cache[file_name] = {
                    item.get("id")
                    for item in conversions
                    if isinstance(item, dict)
                }
            if conversion_id not in pack_cache[file_name]:
                fail(
                    f"{fact_id}: conversion {conversion_id!r} "
                    f"not found in {file_name}"
                )

        note = fact.get("note")
        if note is not None and (
            not isinstance(note, str) or not note.strip()
        ):
            fail(f"{fact_id}: note must be non-empty when present")

    print(
        f"Validated {len(facts)} cat facts, "
        f"catalog {catalog['version']}, facts {document['version']}."
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path("."))
    args = parser.parse_args()
    validate(args.root.resolve())


if __name__ == "__main__":
    main()
