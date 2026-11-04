#!/usr/bin/env python3
import argparse
import hashlib
import json
import re
from pathlib import Path

ID = re.compile(r"^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$")
VERSION = re.compile(r"^[0-9]+(?:\.[0-9]+){1,3}$")
LOCALE = re.compile(r"^[a-z]{2}(?:-[A-Za-z0-9]+)*$")
FACT_PATH = re.compile(
    r"^facts/[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.json$")
IMAGE_PATH = re.compile(
    r"^facts/[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.(?:jpg|jpeg|png|webp)$")
GIT_SHA = re.compile(r"^[0-9a-f]{40}$")
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


def version_tuple(value):
    return tuple(int(part) for part in value.split("."))


def normalized_sha256(path):
    text = path.read_text(encoding="utf-8")
    normalized = text.replace("\r\n", "\n").replace("\r", "\n")
    return hashlib.sha256(normalized.encode("utf-8")).hexdigest()


def git_blob_sha1(path):
    payload = path.read_bytes()
    header = f"blob {len(payload)}\0".encode("ascii")
    return hashlib.sha1(header + payload).hexdigest()


def validate_fact(
    fact,
    label,
    global_ids,
    pack_cache,
    root,
):
    if not isinstance(fact, dict):
        fail(f"{label} must be an object")

    fact_id = fact.get("id")
    if not isinstance(fact_id, str) or not ID.fullmatch(fact_id):
        fail(f"{label}.id is invalid")
    if fact_id in global_ids:
        fail(f"duplicate fact id across catalog: {fact_id}")
    global_ids.add(fact_id)

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
        if (
            not isinstance(file_name, str)
            or not PACK_PATH.fullmatch(file_name)
        ):
            fail(f"{fact_id}: invalid reference file")
        if (
            not isinstance(conversion_id, str)
            or not ID.fullmatch(conversion_id)
        ):
            fail(f"{fact_id}: invalid reference conversionId")

        if file_name not in pack_cache:
            pack = load_json(root / file_name)
            pack_cache[file_name] = {
                item.get("id")
                for item in pack.get("conversions", [])
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

    if catalog.get("contentBaseUrl") != (
        "https://raw.githubusercontent.com/namireki/"
        "catverter-packs/{revision}/"
    ):
        fail(
            "fact catalog contentBaseUrl is not the "
            "canonical immutable base"
        )

    presentation = catalog.get("presentation")
    if presentation is not None:
        if not isinstance(presentation, dict):
            fail("presentation must be an object")
        host = presentation.get("host")
        if not isinstance(host, dict):
            fail("presentation.host must be an object")
        if not isinstance(host.get("name"), str) or not host["name"].strip():
            fail("presentation.host.name must be non-empty")

        image = host.get("image")
        if not isinstance(image, dict):
            fail("presentation.host.image must be an object")
        image_file = image.get("file")
        image_sha = image.get("gitSha")
        if (
            not isinstance(image_file, str)
            or not IMAGE_PATH.fullmatch(image_file)
        ):
            fail("presentation.host.image.file is invalid")
        if (
            not isinstance(image_sha, str)
            or not GIT_SHA.fullmatch(image_sha)
        ):
            fail("presentation.host.image.gitSha is invalid")
        actual_image_sha = git_blob_sha1(root / image_file)
        if image_sha != actual_image_sha:
            fail(
                "presentation host image Git blob SHA mismatch: "
                f"expected {image_sha}, got {actual_image_sha}"
            )

        alt_text = host.get("altText")
        if not isinstance(alt_text, dict) or not alt_text:
            fail("presentation.host.altText must be non-empty")
        if not isinstance(alt_text.get("en"), str) or not alt_text["en"].strip():
            fail("presentation.host.altText requires English")
        for locale, value in alt_text.items():
            if not LOCALE.fullmatch(locale):
                fail(f"presentation.host.altText has invalid locale {locale!r}")
            if not isinstance(value, str) or not value.strip():
                fail(f"presentation.host.altText is empty for {locale}")

    entries = catalog.get("documents")
    if not isinstance(entries, list) or not entries:
        fail("fact catalog documents must be a non-empty array")

    document_ids = set()
    document_files = set()
    global_fact_ids = set()
    pack_cache = {}
    fact_count = 0
    catalog_minimum = version_tuple(catalog["minimumAppVersion"])

    for document_index, entry in enumerate(entries):
        label = f"documents[{document_index}]"
        if not isinstance(entry, dict):
            fail(f"{label} must be an object")

        entry_id = entry.get("id")
        if not isinstance(entry_id, str) or not ID.fullmatch(entry_id):
            fail(f"{label}.id is invalid")
        if entry_id in document_ids:
            fail(f"duplicate fact document id: {entry_id}")
        document_ids.add(entry_id)

        file_name = entry.get("file")
        if (
            not isinstance(file_name, str)
            or not FACT_PATH.fullmatch(file_name)
            or file_name == "facts/catalog.json"
        ):
            fail(f"{entry_id}: invalid fact document file")
        if file_name in document_files:
            fail(f"duplicate fact document path: {file_name}")
        document_files.add(file_name)

        fact_path = root / file_name
        expected_hash = entry.get("sha256")
        actual_hash = normalized_sha256(fact_path)
        if expected_hash != actual_hash:
            fail(
                f"{entry_id}: SHA-256 mismatch for {file_name}: "
                f"expected {expected_hash}, got {actual_hash}"
            )

        document = load_json(fact_path)
        if document.get("documentType") != "catverter-facts":
            fail(f"{file_name} has the wrong documentType")
        if document.get("schemaVersion") != 1:
            fail(f"{file_name} must use schemaVersion 1")

        require_version(
            document.get("version"),
            f"{file_name} version")
        require_version(
            document.get("minimumAppVersion"),
            f"{file_name} minimumAppVersion")

        if (
            version_tuple(document["minimumAppVersion"])
            > catalog_minimum
        ):
            fail(
                f"{file_name} requires app "
                f"{document['minimumAppVersion']}, which is newer than "
                f"catalog minimumAppVersion "
                f"{catalog['minimumAppVersion']}"
            )

        facts = document.get("facts")
        if not isinstance(facts, list) or not facts:
            fail(f"{file_name}: facts must be a non-empty array")

        for index, fact in enumerate(facts):
            validate_fact(
                fact,
                f"{file_name}.facts[{index}]",
                global_fact_ids,
                pack_cache,
                root,
            )
            fact_count += 1

    print(
        f"Validated {fact_count} cat facts across "
        f"{len(entries)} documents, catalog {catalog['version']}."
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path("."))
    args = parser.parse_args()
    validate(args.root.resolve())


if __name__ == "__main__":
    main()
