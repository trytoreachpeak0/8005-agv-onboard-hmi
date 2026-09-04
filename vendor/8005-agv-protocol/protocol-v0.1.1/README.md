# Vendored `8005-agv-protocol` registry

This directory contains the immutable protocol registry snapshot used by the
onboard architecture tests.

- source repository: `trytoreachpeak0/8005-agv-protocol`
- release tag: `protocol-v0.1.1`
- source commit: `1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- registry file: `errors/error-codes.json`

The test reads the `codes[].code` values from the JSON file at runtime. Do not
copy the enum into C#; when the protocol release changes, replace this snapshot
with the corresponding approved release and update the provenance above.
