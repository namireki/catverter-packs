# Catverter pack v1 — compatibility and publication policy

**Status: candidate freeze policy.** This is the proposed long-term contract
for pack documents using `schemaVersion: 1`. It becomes binding for the
first explicitly tagged *pack-v1 contract* release. Until that release the
schema remains a draft. The separate online-catalog transport/profile is not
frozen by this document.

This document defines **behavior beyond JSON Schema**. Always validate with
the shared Catverter reader and the public repository validator before
publishing. A JSON Schema check alone cannot validate UnitsNet semantics,
identity continuity, localization coverage, or safe numeric operations.

## Identity and scope

- `packageId` is the permanent global pack identity. It uses a reverse-DNS
  lowercase identifier (at least three segments), ending in `.<id>`.
  Ownership must be verified for external contributors. The
  `com.namirekistudio.catverter.pack.*` namespace is reserved for official
  packs. Do not assign an existing package ID to a different pack.
- `id` is the stable short name and must be **unique within the catalog**,
  including across different contributor directories. Both `id` and
  `packageId` remain immutable within an existing publication. If two
  contributors want the same local name, one chooses a distinct prefixed ID.
- `conversion.id` and `modifier.id` are permanent within their pack.
  A conversion identity must not be reused for a different quantity **or
  physical unit**. Renaming labels, correcting a numerical reference,
  amending a source, and revising a description do not change its identity.
- Moving a pack between maintainers or changing its official/contributed
  classification is an explicit migration and trust review, not a patch.
  Localizations and saved selections currently reference short IDs; allowing
  short-ID collisions would require a different contract and application
  migration (a potential schema v2 design).

A pack update must **not** replace or masquerade as another pack merely by
sharing a similar display name or author. Only matching `packageId`,
classification, validated catalog provenance and compatible versions can
authorize an update.

## Numbers and physical quantities

1. A reference `value` is a positive, finite JSON number, parsed as a .NET
   `double`. A zero, negative, NaN, or Infinity reference is invalid.
2. `quantity` and `unit` must be exact, supported UnitsNet names, and
   construction by the application quantity factory must succeed.
3. **Temperature reference values use `Kelvin` only.** Celsius and
   Fahrenheit are offset scales, so comparing their values as ratios would
   be physically misleading. Users may continue *entering* Celsius and
   Fahrenheit; this rule applies to pack comparison references.
4. Values are **not globally limited to decimal's numeric range**. Current
   valid v1 packs include `1e-52` square metres and `1e44` joules. Their
   successful representation depends on the specific quantity/unit path.
   The current `Power`/`Watt` path rejects magnitudes greater than
   `decimal.MaxValue` (~`7.922816e28` W); the runtime reader reports
   such values as a typed pack-definition failure.
5. Modifiers are positive finite multipliers; authors should avoid products
   and comparison counts that overflow, underflow, or become nonfinite.
   Runtime conversion refuses nonfinite results even in Chaos mode.
6. Record assumptions and uncertainty in `notes`, with a real source in
   `sourceUrl` where applicable. Approximate, illustrative benchmarks must
   not be represented as exact measurements.

The JSON Schema validates positive numeric syntax. The application and public
validator enforce additional semantic constraints. A valid *reference* does
not imply that every possible user-supplied magnitude can be represented.
Out-of-range calculations must fail safely instead of crashing or displaying
`NaN`/`Infinity`.

## Versions and compatibility promises

| Field | Purpose | Rules |
|---|---|---|
| `schemaVersion` | Pack structure and semantics | Existing v1 documents remain readable after the freeze; incompatible document changes require v2 |
| Pack `version` | Specific pack's content revision | Strictly increasing when published payload changes; three numeric components for new publications |
| `minimumAppVersion` | Lowest compatible Catverter release | Must not decrease on updates; increase if new features, units, or semantics require a newer app |
| Catalog `version` | Entire published snapshot | Must increase when the catalog payload changes; not tied to any pack version |

For pack content versions, use **MAJOR.MINOR.PATCH**, all nonnegative
integers:

- **PATCH**: correct values, text, sources, notes or metadata without
  changing stable identities or adding/removing conversions or modifiers.
- **MINOR**: add conversion/modifier identities without removing old ones.
- **MAJOR**: breaking change to a contributed pack's content, such as removing
  a conversion/modifier, with a documented migration. Major does **not**
  permit silently reusing a conversion ID for a different physical meaning.
- **Official packs** must retain previously shipped conversion and modifier
  IDs regardless of major version. Replacing an official pack's identity or
  retiring entries requires an explicit coordinated app migration/new schema.

An unchanged pack identity **and version** must always address the same
normalized UTF-8 payload hash. Never silently edit a released pack file
without increasing its version. Editorial locale changes update catalog
hashes/version; they do not automatically change pack content versions.

Existing readers support earlier two-to-four-part `.NET Version` strings;
this *authoring requirement* does not retroactively make historical v1
documents invalid. Retain a reader fixture for old v1 content when the pack
contract is frozen.

A compatible Catverter release continues to read valid v1 pack documents,
provided their `minimumAppVersion` is satisfied and their
quantity/unit vocabulary is supported. A new UnitsNet unit may still be
unknown to older apps despite an unchanged JSON schema. Check this explicitly
against the **oldest claimed app version**; the publisher warns about newly
introduced vocabulary but cannot run a private old-app build automatically.

## Publication gates

1. Validate the proposed pack and catalog JSON, quantity/unit semantics,
   origin/classification, locale coverage, and normalized SHA-256 hashes.
2. Compare the proposed public catalog with the current published `main`
   revision using `scripts/check_publication_compatibility.py`. This
   rejects reused IDs, version/hash drift, removal of official identities,
   and insufficient version bumps. New quantity/unit vocabulary produces
   a warning requiring minimum-version review.
3. For official content, update and commit bundled app packs, then use
   `scripts/sync_official_from_app.py` and `--verify` against the
   committed CatverterV2 checkout. Both coordinated PRs must be reviewed.
4. Add historical v1 fixtures to core tests and retain them when new schema
   generations are introduced. Test significant numeric extremes through
   both parsing and the conversion service, not only raw JSON validation.
5. Before declaring a new supported `minimumAppVersion`, test publication
   with a matching older app build/release. No such cross-version test can
   be fully automated until a tagged baseline and a safe CI dependency
   between the private app repository and public data repository exist.

**Limitations:** The public Actions workflow has no access to the private
CatverterV2 repository or its release artifacts. Its delta checker compares
against the published public repository, **not against the private upstream**.
The coordinated source sync remains an explicit release gate. The v1 freeze
should be tagged only after the compatibility suite and first cross-version
validation pass. The public catalog protocol may evolve independently.
