# PharmaLIMS v309 — Trusted Signing Certificate Secret Cutover Plan
**Date:** 2026-11-02  
**Purpose:** Remove the service TLS PFX password from `hostsettings.json` without weakening the trusted-signing boundary.

## Supported target configuration
v309 now supports the trusted signing certificate from `LocalMachine\My` by exact SHA-1 certificate thumbprint:

```json
{
  "CertificatePath": "",
  "CertificatePassword": "",
  "CertificateThumbprint": "<40 HEX THUMBPRINT>",
  "CertificateStoreName": "My"
}
```

`TrustedHostSettings` fails closed unless exactly one certificate source is configured. Store mode accepts only `LocalMachine\My`, requires an exact 40-hex thumbprint, requires a private key, requires the certificate to be currently valid, and requires `CertificatePassword` to be empty.

## Controlled cutover prerequisites
1. Preserve the current service runtime and protected `hostsettings.json` as a rollback set.
2. Record the current server-certificate SHA-256 pin used by the desktop client.
3. Import the approved replacement/current service certificate into `Cert:\LocalMachine\My` with a non-exportable private key where policy allows.
4. Grant private-key read access only to the `PharmaLIMSSigner` service identity, SYSTEM and Administrators as required by the approved certificate-management procedure.
5. Verify certificate subject/SAN, validity, chain, EKU, private-key availability, SHA-256 pin and intended service identity.
6. Because the previous PFX password was exposed during diagnostics, perform the approved certificate/secret rotation and revocation assessment. Merely hiding the old password does not erase prior exposure.

## Cutover
1. Stop `PharmaLIMS.TrustedSigning`.
2. Back up the protected service configuration and record its SHA-256.
3. Set `CertificatePath=""`, `CertificatePassword=""`, the approved `CertificateThumbprint`, and `CertificateStoreName="My"`.
4. Preserve all database targets, pipe name, SID mappings, deployment mode and UAT flags unchanged.
5. Start the service.
6. Verify service status, named pipe availability and client TLS pin.
7. Execute controlled trusted-signing smoke/negative tests. Do not re-use Activity 10; it is already Approved.

## Rollback
If any certificate-store or private-key access check fails:
1. Stop the service.
2. Restore the exact protected configuration/runtime rollback set.
3. Start the service only after validating rollback hashes and ACLs.
4. Record the failed cutover evidence. Do not weaken private-key or service-directory ACLs to force startup.

## Release status
Source support is implemented, but H-02 remains operationally **OPEN** until the Windows certificate-store cutover, exposure/rotation assessment and post-cutover validation are executed and approved.


## Automated controlled cutover (R5)
The source now includes:
- `scripts/Test-V309CertificateStoreReadiness.ps1`
- `scripts/Invoke-V309CertificateStoreCutover.ps1`

The readiness script is read-only and now verifies:
- exact `LocalMachine\My` thumbprint match;
- certificate validity and Server Authentication EKU;
- client SHA-256 pin equality;
- machine private-key path resolution;
- read ACL for the configured Windows service SID.

The cutover script:
1. requires elevation;
2. creates a protected backup of the existing `hostsettings.json`;
3. refuses to proceed unless the readiness evidence proves client-pin and service private-key access;
4. preserves all non-certificate host settings byte-semantically;
5. clears `CertificatePath` and `CertificatePassword`, then configures the exact thumbprint and `My` store;
6. restarts the trusted-signing service and repeats readiness validation;
7. rolls back the exact prior configuration if startup or verification fails;
8. does **not** delete the legacy PFX and does **not** claim certificate/secret rotation is complete.

H-02 can only be closed after the approved exposure/rotation assessment, successful controlled cutover evidence, and QA/security approval.
