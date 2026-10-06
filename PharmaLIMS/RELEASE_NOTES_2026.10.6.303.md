# PharmaLIMS 2026.10.6.303

## Release Scope

- Advances the controlled application identity from 2026.10.6.302 to 2026.10.6.303.
- Adds a QA-controlled Water Evidence Reconciliation action for post-control water samples with missing immutable LimitDescription snapshots.
- Reconciliation is available only for Water samples that are not locked, have no active certificate, have an open Quality Event, and are signed by a user with CanQaApproveResults permission.
- The action restores only blank LimitDescription values when original append-only Water Result Entry audit evidence contains the prior specification and that value exactly matches the approved Water specification effective at sample registration.
- Uses ResultRowVersion concurrency control and records an electronic signature plus append-only audit evidence for each restored row.
- Results, result statuses, remarks, Quality Events, certificates, and workflow status are not changed by the reconciliation action.

## Confirmed Production Evidence

- Production v302 preflight identified 6 post-control Water test rows without immutable LimitDescription evidence; all 6 belong to sample PW-2026-0084.
- The original audit trail for SampleTestID 652-657 shows that each row had the correct MQC-G-0018 v2.0 specification snapshot immediately before the historical Water Result Entry save and that the save cleared the snapshot.
- For all 6 rows, the original audit specification exactly matches the approved Water specification effective on the sample registration date.
- The source defect that allowed result save to overwrite LimitDescription was already corrected on 2026-10-04 by the immutable-snapshot result-save fix; v303 adds the controlled remediation path for the already-affected record.

## Database and Workflow Control

- No database schema migration is introduced by this release.
- No existing Water record is automatically rewritten during deployment or startup.
- Reconciliation is an explicit signed QA action executed inside a transaction and fails closed if the audit evidence, approved registration-time specification, sample status, certificate state, Quality Event state, or rowversion has changed.

## Verification

Release validation requires source-manifest verification, Release build, source-contract tests, review regression, SQL integration, WPF RuntimeSmoke, Production artifact smoke, and live Production preflight confirmation after authorized QA reconciliation.
