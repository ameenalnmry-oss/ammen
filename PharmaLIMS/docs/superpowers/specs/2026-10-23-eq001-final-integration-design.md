# EQ-001 final integration design

Implement the user's ordered final-integration request on PR #62 in the existing checkout. The user explicitly authorized continuous execution without per-step approvals. No production database, production grants, approved record edits, or PR merge is authorized.

## Architecture decision

Use a local Windows named pipe with an explicit non-network ACL, kernel-derived client SID, and TLS server authentication. The service alone holds a private SQL connection and certificate. A protected service configuration maps Windows SIDs to PharmaLIMS usernames; no payload may supply the authenticated username. The WPF client validates the TLS chain/name and pins the configured service certificate before sending a fresh PharmaLIMS password. The service independently checks password, current permissions and lockout, then invokes the existing transactional SQL store. Client-proposed equipment JSON is never signature evidence.

The service runs under a separate Windows account. Reject caller SID equal to the service account SID. Bind the expected PharmaLIMS application username to the mapped account, preventing signatures in a different user's logged-in application session. Bound message sizes, per-request deadlines, sanitized errors and cancellation keep the IPC boundary fail-closed. The private service files require service/admin-only ACLs; no credentials enter Git.

Alternatives considered: HTTPS/Negotiate adds remote hosting/SPN configuration to a local workflow; unsigned/plain local IPC does not authenticate the service before disclosure of passwords. TLS over a native Windows pipe supports both server authentication and OS-derived caller identity without an external identity provider.

## UI and compatibility

Replace only LabResources equipment saves with a dedicated trusted signature dialog and client; remove its SQL write/signature fallback in all build configurations. Preserve other ElectronicSignature workflows, result review, permissions and existing APIs. When trusted signing is enabled, route login, reauthentication, self-service password changes and user administration through the independently protected service as well: the desktop SQL principal must have no credential/counter writes or password-material reads. Reuse the original authentication and administration implementation inside the host. Administration requires a single-use 30-second proof bound to the kernel caller SID, verified username and current account rowversion; the server supplies the audit actor/signer. Trusted signing remains disabled by default until explicitly configured; production activation remains a deployment/QA action after acceptance.

## Validation

Keep the accepted 25-check SQL report and full log in docs/validation/EQ001. Add new transport and UI-boundary tests before implementation. CI uses a distinct Windows service account, trusted test certificate and isolated SQL UAT database. Verify impersonation refusal, unmapped identities, unauthorized/locked users, incorrect password counters, success create/update, replay and concurrency rejection, immutable evidence and historical rows unchanged, and service-unavailable behavior. Exercise real WPF controls and the service; fixed/fake identities do not establish Windows E2E acceptance. Existing tests rerun when their covered application/dependency/authentication code changes. Store artifacts and exact commit/run IDs. No readiness claim without completed Windows UAT.
