# Pack schema v1 — freeze readiness and release baseline

**Status: candidate, not tagged or frozen.** Prepared on 2026-10-29.
The structural `schemaVersion: 1` stays unchanged. The online catalog
transport format is **not** frozen by the pack-document freeze.

## Historical compatibility evidence

| Reference | Version claim | Reproducible artifact | Verification |
|---|---|---|---|
| CatverterV2 commit `3a2aa7a6b272ca2b9413297e880f8d45a54dfea8` (2026-10-26) | `ApplicationDisplayVersion 1.0` | Unmodified `packs/cats.json`; Git blob `dab94d34c44de3d48956d8d3fa844b0035e5d6de` | Preserved in CatverterV2 `test-fixtures/pack-v1-2026-10-26-cats.json`; its current v1 reader and round-trip tests must pass |
| Current CatverterV2 | `ApplicationDisplayVersion 1.1.0` | 12 input quantity families, including Power | CI must validate all shipped packs with the current reader, including extreme finite values |
| Current public catalog | `minimumAppVersion 1.1.0` | Expanded official packs and contributor pack | Each current pack now independently declares `minimumAppVersion 1.1.0`; version and hashes are updated in paired PRs |

The historical source revision is **not a tagged public release**, nor
proof that a distributable 1.0 app exists. Neither repository currently has
a published release or tag. The only demonstrated historic baseline is
the **source commit** above. It is useful for validating old-v1-to-current-v1
readability; it is *not* evidence that the old app understands today's new
quantity families.

At the historical v1 source revision, the unit picker exposed **Mass,
Length, Volume and Speed**. The expanded families (Area, Temperature,
Acceleration, Duration, Frequency, BitRate, Illuminance, Power) are
conservatively assigned a minimum version of **1.1.0**. A future
unrecognized quantity still requires an explicit compatibility review; the
checker cannot infer when a newly introduced unit became available.

## Freeze gates

Before applying the first immutable pack-schema-v1 tag:

- [ ] The CatverterV2 minimum-version/fixture PR is merged.
- [ ] The corresponding `catverter-packs` publication-policy and synchronized
      minimum-version PR is merged.
- [ ] Application CI confirms current core tests and all supported build
      targets pass, including historical-v1 fixture and conversion tests.
- [ ] Public repository CI confirms the schema/content validator and
      publication-delta compatibility tests pass.
- [ ] The 11 official public packs match the committed application source,
      including version, normalized SHA-256, identifiers and minimum app
      version. Run
      `python scripts/sync_official_from_app.py --app ../CatverterV2 --verify`.
- [ ] Identify and retain the **oldest actually distributed, reproducible
      app build** claiming support for the v1 schema. If that build does not
      exist, create the first signed/tested app 1.1.0 release baseline and
      record its build revision and SHA-256.
- [ ] Run a real old/current compatibility smoke test: old-v1 fixture in the
      new reader, supported current packs in the declared minimum app build,
      and confirmed safe rejection of unsupported newer content by the
      historic 1.0 source/runtime.
- [ ] Confirm both repositories' default branches include the reviewed and
      synchronized content; review the final catalog, version and schema
      diffs before tagging.
- [ ] Record the final default-branch commit SHAs, test results, build
      artifacts, and designated minimum-supported app release in the freeze
      release notes.

These checks must not be replaced by a successful schema-only parse.
The cross-version runtime check currently requires an actual app build
because no tagged 1.0/1.1 release artifact is available.

## Tag procedure (once all gates are complete)

Create **annotated, immutable release tags** on the reviewed default-branch
commits. Do not tag a PR branch or reuse an existing tag:

```sh
# Run in the public catverter-packs checkout after its PR is merged:
git fetch --tags origin
git switch main
git pull --ff-only
git tag -a pack-schema-v1.0.0 -m "Freeze Catverter pack document schema v1"
git push origin pack-schema-v1.0.0

# Run in the private CatverterV2 checkout after its PR is merged:
git fetch --tags origin
git switch master
git pull --ff-only
git tag -a pack-schema-v1.0.0 -m "Baseline Catverter v1 pack reader and bundled packs"
git push origin pack-schema-v1.0.0
```

After a successful freeze, update documentation to identify the exact tag
and remove draft warnings for the **pack document only**. Breaking
structural/semantic changes must use `schemaVersion: 2`; the v1 reader
must continue to accept frozen v1 fixtures. Changes to online catalogs,
localizations and contributions can continue under their own versioning
and compatibility rules.

## Limitations

- The public repository cannot independently fetch or build the private
  app through its normal workflow.
- The publication checker compares against the public `main` baseline and
  enforces a known minimum-app quantity floor; it cannot prove arbitrary
  future unit support by historical app binaries.
- The current automation does **not** create a tag; tagging is a deliberate
  separate release action after the evidence above is satisfied.
