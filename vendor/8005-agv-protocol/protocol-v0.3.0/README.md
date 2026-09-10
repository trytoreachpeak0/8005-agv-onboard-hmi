# Vendored `8005-agv-protocol` registry

This directory contains an immutable snapshot of the pinned protocol release.

- source repository: `trytoreachpeak0/8005-agv-protocol`
- release tag: `protocol-v0.3.0`
- source commit: `345c53c58517968192c87c3e7777ed08ddb48726`
- registry file: `errors/error-codes.json`
- slice index: `integration-slices/index.json`
- JSON Schemas: `schemas/` (all 60 files) and `manifest/release.json`

The architecture tests read the `codes[].code` values from the registry file at runtime. Do not
copy the enum into C#; when the protocol release changes, replace this snapshot with the
corresponding approved release and update the provenance above.

`schemas/` and `manifest/release.json` are read by `tools/SQCD.Agv.SchemaConformance`, which
validates every outbound line the G2 tests send (8005-agv-program#34). They are not trusted as
files: the validator first hashes the manifest against `WireToGateRelease.ManifestSha256` and
recomputes `WireToGateRelease.SchemaBundleSha256` over `schemas/` the way the protocol repository's
`tools/finalize-manifest.mjs` defines it, and refuses to run on a mismatch. `.gitattributes` marks
this tree `-text` so a checkout keeps the tag's exact bytes. The git tree id of `schemas/` equals
`git -C <protocol repository> rev-parse protocol-v0.3.0:schemas`.
