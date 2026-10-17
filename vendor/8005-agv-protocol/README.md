# 8005-agv-protocol

Shared executable protocol contracts for the 8005 AGV full product
(`AGV_FULL_PRODUCT`).

## Current state

The repository currently contains an **unapproved `5.0.0` superseding content
snapshot** for `ProtocolVersion = 6` and profile `AGV_FULL_PRODUCT`.
`protocol-v4.0.0` remains immutable, and `protocol-v5.0.0` does not exist yet.

This candidate is **breaking** against `protocol-v4.0.0`
(`BREAKING_PROTOCOL_VERSION_INCREASE`, `INCOMPATIBLE_EXACT_IDENTITY_REQUIRED`):
one change — `WIRE_TO_FULL_INSPECTION`, wire bonding machines to the
full-inspection area, appended to the closed enum `TransportTaskType`, so a
`4.0.0` implementation rejects a worklist carrying it and both implementations
move to `5.0.0` together; `PublicStationFunction` is unchanged (stated in
[`docs/wire-notes.md`](docs/wire-notes.md)). See
[`compatibility/report.json`](compatibility/report.json) for the full list.

It is not a formal `ProtocolRelease` until a release approval (the product
owner, or an AI agent the product owner authorized) covers its exact commit,
manifest, vectors hash and compatibility classification. Both sides consume it
as a candidate: `ApprovalStatus = SUPERSEDING_CANDIDATE`, and gate evidence
produced on it is `UNRELEASED_CANDIDATE` and does not count towards a batch
exit.

Machine-readable JSON Schema, the message manifest, error registry, valid and
invalid examples, deterministic trajectories, runner/result contracts, and the
integration-slice index are authoritative. Markdown is explanatory only.

```powershell
pnpm install --frozen-lockfile
pnpm manifest:finalize
pnpm g1
```

See:

- [`manifest/release.json`](manifest/release.json)
- [`attestations/release-approval.template.json`](attestations/release-approval.template.json)
- [`docs/README.md`](docs/README.md)
- [`docs/release-governance.md`](docs/release-governance.md)
- [`docs/candidate-limitations.md`](docs/candidate-limitations.md)
- [`compatibility/report.json`](compatibility/report.json)
- [`compatibility/implementation-version-matrix.json`](compatibility/implementation-version-matrix.json)
- [`evidence/g1-result.json`](evidence/g1-result.json)

G1 PASS proves candidate-internal consistency only. It does not prove G0 human
approval, either product implementation, G2/G3, real RIoT, real IO, target
hardware, or factory qualification.
