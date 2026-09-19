# PharmaLIMS v280 - Release Acceptance / UAT

هذا المستند لا يمثل نتيجة PASS تلقائية. يتم تعبئته بعد التنفيذ الفعلي على Windows + SQL Server.

## Database Readiness
- [ ] All checksum-controlled migrations applied.
- [ ] Full System Preflight: 0 BLOCKER.
- [ ] Users.AuthenticationRowVersion = ROWVERSION NOT NULL.
- [ ] Users.MustChangePassword = BIT NOT NULL.
- [ ] Users.PasswordChangedAt = DATETIME2(0) NULL.
- [ ] EM_Events.ResultRowVersion = ROWVERSION NOT NULL.
- [ ] EM_EventPlates.ResultRowVersion = ROWVERSION NOT NULL.
- [ ] SampleTests.ResultRowVersion = ROWVERSION NOT NULL.

## Operational acceptance
- [ ] Clean Windows .NET 8 WPF Release build.
- [ ] ReviewRegression and DatabaseIntegration pass against representative SQL Server/LocalDB.
- [ ] Login / legacy credential migration / lockout / unlock / forced password change / e-signature.
- [ ] User Management permissions, separation of duties, reset/unlock and concurrent edits.
- [ ] Water end-to-end workflow and concurrency.
- [ ] EM end-to-end workflow and concurrency.
- [ ] PRM end-to-end workflow, QE, certificate and controlled reissue.
- [ ] Audit Trail evidence review.
- [ ] A4 report/certificate rendering and printing.
- [ ] QA approval of executed evidence.
