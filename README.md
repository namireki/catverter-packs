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

The pack document contract remains schema v1. The catalog's trust,
provenance, locale, and immutable-download fields are optional schema-v1
extensions for compatibility with existing readers, but this repository's
semantic validator requires them on the canonical catalog.

The current catalog contains 11 official packs derived from CatverterV2
commit `6dd430bfda08211d27aa7212526567f186d70004`, one contributed pack, at least
ten Frequency conversions and five themed Duration conversions in every
cataloged pack, and complete French and Italian locales for the official set. Official source
hashes are kept in each entry's `source` object; the entry's top-level
`sha256` covers the relocated file in this repository.

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
