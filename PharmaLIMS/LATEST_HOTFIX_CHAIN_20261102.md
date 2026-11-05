# PharmaLIMS v309 - latest consolidated source

This source tree consolidates the UAT hotfix chain used in this conversation:

1. 20261031 primary-identity activity-signing hotfix (base full source).
2. Trusted Admin diagnostics hotfix.
3. Canonical Activity reauthentication hotfix.

The canonical Activity patch is applied after the Trusted Admin diagnostics patch; where both
modify the same file, the canonical version is retained because it already contains the safe
Trusted Admin rejection-category changes plus the later Activity reauthentication changes.

No additional database migration is introduced by the final canonical reauthentication patch.
The earlier 20261031_001 migration remains part of the source tree from the base full-source package.
