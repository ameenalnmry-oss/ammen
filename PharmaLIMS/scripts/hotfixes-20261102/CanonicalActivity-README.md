# PharmaLIMS v309 — Canonical Activity Reauthentication Hotfix

Purpose:
- Removes the independent Activity password verifier from the live signing path.
- Activity signing now reauthenticates the Windows-bound PharmaLIMS user through the same trusted AuthService credential verifier used by Login/Change Password.
- Preserves Activity DB authority for activity state, local UAT permission, SoD, evidence, rowversion, and signature integrity.
- Does not copy password hashes between databases.
- Does not modify Activity 10 or any signature.
- No database migration is required.

Deployment:
1. Close all PharmaLIMS.exe processes.
2. Run PowerShell as Administrator.
3. Execute Deploy-V309CanonicalActivityReauthHotfix.ps1.
4. The script builds before touching runtime, takes source/runtime backups, and restores runtime if deployment fails.

Focused static regression:
- test_v309_trusted_activity_reauthentication.py
- test_v308_activity_signing_split.py
- 18/18 passed in the preparation environment.

Note:
The preparation environment did not contain the .NET SDK, so the deployment script performs the authoritative dotnet build on the UAT workstation before changing runtime.
