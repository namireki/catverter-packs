# Catverter standard packs

This repository is the canonical source for Catverter's externally updateable
standard packs. Every catalog entry is explicitly classified as either
`official` or `contributed`; clients must not infer trust from pack names,
authors, paths, or display text.

The repository root contains `catalog.json`. Contribution files live under
`packs/<author>/<pack-id>.json`, keeping each contributor's packs together and
the root uncluttered. All public JSON documents currently use
`schemaVersion: 1`; content versions such as `1.0.0` are versioned
independently.

See the [pack v1 compatibility and publication policy](docs/pack-v1-contract.md)
for ID scope, safe physical ratios, version-bump rules, and minimum-app
compatibility requirements. This is the **candidate freeze contract**, not
yet a tagged guarantee. Public CI also compares proposed pack changes to
the currently published `main` catalog and rejects identity reuse or
silent version changes.

The pack document contract remains schema v1. The catalog's trust,
provenance, locale, and immutable-download fields are optional schema-v1
extensions for compatibility with existing readers, but this repository's
semantic validator requires them on the canonical catalog.

The catalog contains **11 official packs** synchronized from the current
bundled CatverterV2 packs and **one opt-in contributed pack** (`office-life`).
All **12 packs contain 20 Power conversions**, at least ten Frequency
conversions, and five themed Duration conversions. Official packs also
include the newer Area, Temperature, Acceleration, BitRate, and Illuminance
families. The official source commit and bundled catalog version are recorded
in `catalog.json.sourceCatalog` rather than hard-coded in this README.

French and Italian locale entries cover every official conversion, although
newly imported comparison names still use English fallback wording when a
reviewed translation is unavailable. Improving these translations remains a
separate localization task. Official source hashes are stored in each
`source` object; the top-level `sha256` hashes the relocated public file.

## Catalog and download contract

The stable branch is `main`. Consumers must resolve that branch to a commit SHA,
then fetch the catalog and every selected pack from that same immutable
revision:

1. Resolve `main` with
   `https://api.github.com/repos/namireki/catverter-packs/commits/main` and read
   the response `sha`.
2. Replace `{revision}` in `catalog.json.contentBaseUrl` with that SHA.
3. Fetch `<contentBaseUrl>catalog.json`.
4. Resolve each entry's repository-relative `file` against the same base URL.
5. Decode strict UTF-8, normalize CRLF and CR line endings to LF, compute
   lowercase SHA-256, and require it to match the entry's `sha256`.
6. Require the downloaded pack's `packageId`, `id`, and `version` to match the
   catalog entry before making it visible.

An official update should be staged in full and installed only after every
required pack validates. If any fetch, schema, identity, or hash check fails,
the client keeps its current set or bundled snapshot. This gives the client an
atomic repository snapshot rather than mixing files from different commits.

`official` entries are maintained by Namireki Studio, are eligible for
automatic update, and use the `namireki` maintainer and reserved
`com.namirekistudio.catverter.pack.*` namespace. `contributed` entries are
discoverable but must remain opt-in. A contributor may be Namireki Studio; the
classification, not the maintainer name, controls update behavior. The
validator rejects contributed entries that use the reserved official package
namespace.

Official entries retain their CatverterV2 repository, revision, author-scoped
path, and SHA-256 under `source`. `sha256` always covers the file at this
repository's current `file` path. Catalog locale entries use the same hash and
provenance rules, while their source revision may identify a later upstream
localization commit. Clients may apply a locale only after its catalog hash and
locale identity validate.

Consumers should treat the bundled snapshot as the initial official
installation and the offline fallback. A valid remote official entry with the
same `packageId` is an update candidate rather than a duplicate; compare its
content version, stage the remote file, and replace the bundled copy only after
validation. Contributed entries must never replace official package IDs, must
not become enabled merely because they appear in the catalog, and should be
downloaded only after explicit user opt-in.

## Synchronize bundled official packs

**Update both repositories before releasing an app with changed bundled packs.**
The source of truth for built-in official packs is the CatverterV2
`packs/` tree. An uncoordinated remote catalog may be older than the new
app. Current Catverter builds reject older official pack versions and reject
remote replacements that drop bundled conversion identities, preserving the
offline pack set rather than silently removing quantities.

To publish an official pack change:

1. In CatverterV2, modify the bundled pack JSON and its `version`; update
   `packs/catalog.json` including the pack SHA-256 hashes and catalog
   `version`. Maintain `packs/locales/it.json` and editorial
   `packs/locales/fr.json` so all official conversion IDs are covered.
2. Commit the CatverterV2 changes and check out that commit locally.
3. In this repository, with the CatverterV2 checkout next to it, run:

   ```pwsh
   python scripts/sync_official_from_app.py --app ../CatverterV2
   ```

   The script relocates all official pack files, synchronizes both locale
   files, updates the source commit SHA and source hashes, and increments the
   canonical catalog version. It refuses dirty source packs and incomplete
   locale coverage. **It does not overwrite contributed packs.**
4. Update any contributed pack separately (such as `office-life`) and
   recompute its `catalog.json` hash. Run the validator:

   ```pwsh
   dotnet run --project tools/Catverter.PackValidator -- validate-pack-repository .
   python scripts/sync_official_from_app.py --app ../CatverterV2 --verify
   ```

5. Submit coordinated PRs in CatverterV2 and here. Merge the app source PR
   first, then the matching canonical publication PR, to keep provenance
   resolvable. Do not ship the new app until both have merged.

The `--verify` mode compares all generated official pack and locale
files and source metadata without writing. App-only changes that introduce
new quantity families must **not** be considered finished until the canonical
copy and its locales validate.

## Add a pack

1. Choose a stable lowercase author slug, normally your GitHub username.
2. Copy `templates/pack.v1.json` to
   `packs/<author>/<pack-id>.json` and change its `$schema` reference to
   `../../schemas/catverter-pack-v1.schema.json`.
3. Give the pack a stable lowercase ID and globally unique reverse-DNS
   `packageId`.
4. Add useful conversions across the small, ordinary, and large ranges of each
   supported quantity.
5. Add the pack to `catalog.json` with `classification: "contributed"`, the
   same author slug as `maintainer`, and matching ID, package ID, version, and
   `packs/<author>/<pack-id>.json` path.
6. Compute SHA-256 from UTF-8 JSON after normalizing line endings to LF and put
   the lowercase hash in the catalog entry.

Pack values must use invariant UnitsNet quantity and unit names. Sources,
assumptions, and approximation notes should be recorded in `sourceUrl` and
`notes` when relevant.

## Validate locally

Use the standalone validator included in this repository:

```pwsh
dotnet run --project tools/Catverter.PackValidator -- validate-pack-repository .
```

Refresh catalog hashes with:

```pwsh
dotnet run --project tools/Catverter.PackValidator -- update-catalog .
```

Pull requests run the same validation in GitHub Actions.

## License

Official repository content is available under the included Apache License
2.0. External media is not automatically covered and requires explicit
permission or a separately documented license.
