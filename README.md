# Catverter community packs

This repository hosts externally updateable Catverter conversion packs.

The repository root contains `catalog.json` and every pack referenced by that
catalog. All public JSON documents currently use `schemaVersion: 1`; content
versions such as `1.0.0` are versioned independently.

## Add a pack

1. Copy `templates/pack.v1.json` to `<pack-id>.json`.
2. Give the pack a stable lowercase ID and globally unique reverse-DNS
   `packageId`.
3. Add useful conversions across the small, ordinary, and large ranges of each
   supported quantity.
4. Add the pack to `catalog.json` with the same ID, package ID, and version.
5. Compute SHA-256 from UTF-8 JSON after normalizing line endings to LF and put
   the lowercase hash in the catalog entry.

Pack values must use invariant UnitsNet quantity and unit names. Sources,
assumptions, and approximation notes should be recorded in `sourceUrl` and
`notes` when relevant.

## Validate locally

Use the standalone validator from a sibling Catverter checkout:

```pwsh
dotnet run --project ../CatverterV2/tools/Catverter.ContentTool -- validate-pack-repository .
```

Refresh catalog hashes with:

```pwsh
dotnet run --project ../CatverterV2/tools/Catverter.ContentTool -- update-catalog .
```

Pull requests run the same validation in GitHub Actions.

## License

Official repository content is available under the included Apache License
2.0. External media is not automatically covered and requires explicit
permission or a separately documented license.
