"""Contract regression tests for public pack publication transitions."""

import copy
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from check_publication_compatibility import check_transition


def fixture():
    pack = {
        "schemaVersion": 1,
        "packageId": "com.namirekistudio.catverter.pack.cats",
        "id": "cats",
        "version": "1.9.0",
        "minimumAppVersion": "1.0.0",
        "name": "Cats",
        "conversions": [
            {"id": "cat", "quantity": "Mass", "unit": "Kilogram", "value": 5},
            {"id": "kitten", "quantity": "Mass", "unit": "Kilogram", "value": 1},
        ],
        "modifiers": [{"id": "tiny", "multiplier": 0.5}],
    }
    catalog = {
        "schemaVersion": 1,
        "repositoryId": "com.namirekistudio.catverter.packs",
        "minimumAppVersion": "1.0.0",
        "version": "2.4.1",
        "packs": [
            {
                "id": "cats",
                "packageId": pack["packageId"],
                "classification": "official",
                "version": pack["version"],
                "file": "packs/namireki/cats.json",
                "sha256": "a" * 64,
            }
        ],
    }
    return catalog, {"cats": pack}


class PublicationContractTests(unittest.TestCase):
    def setUp(self):
        self.old, self.old_docs = fixture()
        self.new = copy.deepcopy(self.old)
        self.new_docs = copy.deepcopy(self.old_docs)

    def check(self):
        return check_transition(
            self.old, self.old_docs, self.new, self.new_docs)

    def publish(self, content_version="1.9.1", catalog_version="2.4.2"):
        self.new_docs["cats"]["version"] = content_version
        self.new["packs"][0]["version"] = content_version
        self.new["packs"][0]["sha256"] = "b" * 64
        self.new["version"] = catalog_version

    def test_unmodified_publication_is_valid(self):
        errors, warnings = self.check()
        self.assertEqual([], errors)
        self.assertEqual([], warnings)

    def test_patch_correction_with_new_content_version_is_valid(self):
        self.publish()
        self.new_docs["cats"]["conversions"][0]["value"] = 5.1
        errors, _ = self.check()
        self.assertEqual([], errors)

    def test_unchanged_content_version_must_not_point_to_new_payload(self):
        self.new["packs"][0]["sha256"] = "b" * 64
        self.new["version"] = "2.4.2"
        errors, _ = self.check()
        self.assertTrue(any("unchanged/non-increasing" in e for e in errors))

    def test_changed_catalog_always_requires_catalog_version_bump(self):
        self.new["packs"][0]["sha256"] = "b" * 64
        errors, _ = self.check()
        self.assertTrue(any("catalog changed without increasing" in e for e in errors))

    def test_new_conversion_requires_minor_bump(self):
        self.publish()
        self.new_docs["cats"]["conversions"].append({
            "id": "tiger", "quantity": "Mass", "unit": "Kilogram",
            "value": 200,
        })
        errors, _ = self.check()
        self.assertTrue(any("minor or major" in e for e in errors))

        self.publish(content_version="1.10.0")
        errors, _ = self.check()
        self.assertEqual([], errors)

    def test_existing_conversion_id_cannot_change_physical_meaning(self):
        self.publish(content_version="2.0.0")
        self.new_docs["cats"]["conversions"][0]["quantity"] = "Length"
        self.new_docs["cats"]["conversions"][0]["unit"] = "Meter"
        errors, _ = self.check()
        self.assertTrue(any("physical unit/quantity" in e for e in errors))

    def test_official_pack_must_preserve_bundled_conversion_identities(self):
        self.publish(content_version="2.0.0")
        self.new_docs["cats"]["conversions"].pop()
        errors, _ = self.check()
        self.assertTrue(any("official pack cats" in e for e in errors))

    def test_modifier_removal_requires_major_for_contributors(self):
        self.old["packs"][0]["classification"] = "contributed"
        self.new["packs"][0]["classification"] = "contributed"
        self.publish(content_version="1.10.0")
        self.new_docs["cats"]["modifiers"] = []
        errors, _ = self.check()
        self.assertTrue(any("major content-version" in e for e in errors))

        self.publish(content_version="2.0.0")
        errors, _ = self.check()
        self.assertEqual([], errors)

    def test_reassigning_package_identity_is_blocked(self):
        self.new["packs"][0]["packageId"] = "org.example.catverter.pack.cats"
        self.new_docs["cats"]["packageId"] = "org.example.catverter.pack.cats"
        self.publish()
        errors, _ = self.check()
        self.assertTrue(any("packageId cannot be reassigned" in e for e in errors))

    def test_decreasing_minimum_app_version_is_blocked(self):
        self.old_docs["cats"]["minimumAppVersion"] = "1.5.0"
        self.new_docs["cats"]["minimumAppVersion"] = "1.0.0"
        self.publish()
        errors, _ = self.check()
        self.assertTrue(any("minimumAppVersion cannot decrease" in e for e in errors))

    def test_unknown_quantity_unit_warns_about_old_app_support(self):
        self.publish(content_version="1.10.0")
        self.new_docs["cats"]["conversions"].append({
            "id": "lumens", "quantity": "LuminousFlux", "unit": "Lumen",
            "value": 50,
        })
        errors, warnings = self.check()
        self.assertEqual([], errors)
        self.assertTrue(any("minimumAppVersion" in w for w in warnings))

    def test_new_vocabulary_with_increased_minimum_version_does_not_warn(self):
        self.publish(content_version="1.10.0")
        self.new_docs["cats"]["minimumAppVersion"] = "1.1.0"
        self.new_docs["cats"]["conversions"].append({
            "id": "lumens", "quantity": "LuminousFlux", "unit": "Lumen",
            "value": 50,
        })
        errors, warnings = self.check()
        self.assertEqual([], errors)
        self.assertEqual([], warnings)

    def test_removing_a_pack_requires_separate_migration(self):
        self.new["version"] = "2.5.0"
        self.new["packs"] = []
        self.new_docs = {}
        errors, _ = self.check()
        self.assertTrue(any("published pack removal" in e for e in errors))

    def test_unchanged_catalog_with_only_new_version_is_allowed(self):
        self.new["version"] = "2.4.2"
        errors, _ = self.check()
        self.assertEqual([], errors)


if __name__ == "__main__":
    unittest.main()
