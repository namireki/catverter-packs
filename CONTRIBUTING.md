# Contributing packs

All public documents currently use schema v1. Start from
`templates/pack.v1.json` and keep the stable `id`, `packageId`, and content
`version` synchronized with `catalog.json`.

Use invariant UnitsNet quantity and unit names. Include a useful range of
values, document approximations, cite published reference values with HTTPS
`sourceUrl` entries, and use only text or media you have permission to submit.

When a pack changes:

1. Increment the pack content version.
2. Update the matching catalog version.
3. Recompute the canonical lowercase SHA-256 using UTF-8 JSON with LF line
   endings and no byte-order mark.
4. Increment the catalog content version.

Unless explicitly stated otherwise, contributions submitted for inclusion are
provided under Apache License 2.0 as described by Section 5 of the license.
