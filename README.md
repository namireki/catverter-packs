# Catverter community packs

This repository hosts externally updateable Catverter conversion packs.

The repository root contains `catalog.json`. Contribution files live under
`packs/<author>/<pack-id>.json`, keeping each contributor's packs together and
the root uncluttered. All public JSON documents currently use
`schemaVersion: 1`; content versions such as `1.0.0` are versioned
independently.

## Add a pack

1. Choose a stable lowercase author slug, normally your GitHub username.
2. Copy `templates/pack.v1.json` to
   `packs/<author>/<pack-id>.json` and change its `$schema` reference to
   `../../schemas/catverter-pack-v1.schema.json`.
3. Give the pack a stable lowercase ID and globally unique reverse-DNS
   `packageId`.
4. Add useful conversions across the small, ordinary, and large ranges of each
   supported quantity.
5. Add the pack to `catalog.json` with the same ID, package ID, version, and
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
