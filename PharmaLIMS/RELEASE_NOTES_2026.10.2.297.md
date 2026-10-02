# PharmaLIMS 2026.10.2.297

## Controlled release identity closure

- Advances the controlled application identity from `2026.10.1.296` to `2026.10.2.297` without changing historical database migration version keys or migration SQL.
- Establishes a distinct release identity for source corrections merged after v296.
- Includes PR #30: PRM registration lists are isolated by workflow, with defensive rejection of cross-workflow records.
- Includes PR #31: changing PRM workflow clears prior sample fields, superseded asynchronous list responses are ignored, PRM repository initialization is deferred until database use, and edited PRM sampling time cannot move after a signed laboratory receipt.
- No additional functional feature is introduced by this release-identity change.

## Release state

- Source baseline before identity closure: `355f4e45f523cd1ae9aabb1390bc3574c2aff367` on `main`.
- CI #285 passed on that baseline: 585 source/regression tests, 170 Review Regression tests, Release builds, SQL integration, Development WPF startup smoke, Trend/PDF smoke, Sample Register smoke, PRM workflow reset smoke, Primary Packaging smoke, and NuGet vulnerability checks.
- Controlled migration count and all historical migration SQL/checksums remain unchanged.

## Production release gate

- Source validation and CI success do not by themselves approve this release for Production use.
- Final Production release requires the controlled `workflow_dispatch` path from `main`: materialize and validate protected Production configuration, publish self-contained `win-x64`, apply configured Authenticode signing policy, smoke-test the exact published artifact against the controlled SQL Server target, generate and verify the publish hash manifest and resolved CycloneDX SBOM, attest provenance, and upload the controlled package.
- Site UAT, actual printing verification, operational load evidence, and QA approval remain release-acceptance activities.
