# Contributing packs

All public documents currently use schema v1. Start from
`templates/pack.v1.json` and keep the stable `id`, `packageId`, and content
`version` synchronized with `catalog.json`.

Store each contribution at `packs/<author>/<pack-id>.json`, where `<author>` is
a stable lowercase slug such as your GitHub username. A contributor may own
multiple pack files in the same author folder. Pack files directly in the
repository root are not accepted.

Catalog entries for submissions must use `classification: "contributed"` and
set `maintainer` to the same author slug used by the pack folder. The
`com.namirekistudio.catverter.pack.*` package namespace is reserved for
official packs. Classification is a trust decision made during repository
review and must never be inferred from display names or maintainer names.

Use invariant UnitsNet quantity and unit names. Include a useful range of
values, document approximations, cite published reference values with HTTPS
`sourceUrl` entries, and use only text or media you have permission to submit.

Locale files live at `locales/<locale>.json` and must be listed in
`catalog.json`. A locale may omit contributed packs, but every official pack
must have a localized name, description, and `one`/`other` forms for every
conversion. Each official modifier must be provided either in the locale's
global modifier map or in that pack's modifier map, and every modifier form
must contain `{unit}`.

For **official** packs, coordinate this public repository with the private
CatverterV2 bundled pack changes. After updating and committing
`CatverterV2/packs/`, run from this repository:

```pwsh
python scripts/sync_official_from_app.py --app ../CatverterV2
python scripts/sync_official_from_app.py --app ../CatverterV2 --verify
```

The sync tool copies official pack JSON, Italian and French editorial locale
sources, source commit provenance, and hashes; it leaves contributed packs
alone. Both locale files must cover every official conversion, including all
20 Power entries per pack. Keep the application and public source PRs paired
so the app cannot regress to older online pack versions. Newly authored
English fallback translations require later linguistic review.

**Power numeric safety:** The current application may convert Power values
through .NET `decimal`. For `Power` / `Watt` references, the validator
rejects magnitudes above `decimal.MaxValue` (approximately
`7.9228 × 10^28 W`). Keep published illustrative comparisons in a safely
representable range, rather than relabeling extremely luminous objects with
incorrectly reduced powers. Update the app and public pack copies together.

When a pack changes:

1. Increment the pack content version.
2. Update the matching catalog version.
3. Recompute the canonical lowercase SHA-256 using UTF-8 JSON with LF line
   endings and no byte-order mark.
4. Increment the catalog content version.

Refresh catalog hashes and validate the complete repository with:

```pwsh
dotnet run --project tools/Catverter.PackValidator -- update-catalog .
dotnet run --project tools/Catverter.PackValidator -- validate-pack-repository .
```

Unless explicitly stated otherwise, contributions submitted for inclusion are
provided under Apache License 2.0 as described by Section 5 of the license.
