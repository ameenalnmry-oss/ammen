# Investigation concurrency SQL regression

Portable .NET 8 / SQL Server regression. Creates a uniquely named disposable database and drops only that database in `finally`.

Set `EQ001_SQL_TEST_SETTINGS` to JSON containing `MasterConnectionString`, or `server` and `password` for the test administrator. Configure certificate trust normally; connection validation remains enabled. Run:

```sh
dotnet run --project tests/PharmaLIMS.InvestigationConcurrencyRegression -- /absolute/path/to/PharmaLIMS
```

The harness extracts the actual generic/PRM production header UPDATE SQL, links the production concurrency guard when present, and checks that the corresponding WPF persistence method calls it. It executes stale-content mutations in SQL, invokes the production guard, executes the production UPDATE inside a rolled-back transaction, and checks rejection. It also tests unchanged content, signature preservation, and competing writes/phantom inserts blocked by retained locks from a second SQL connection. Generic Submit/Close tests simulate a second editor during the validation/signature callback after the draft token was captured, verify the window forwards that token into the final helper, and execute the actual final status UPDATE after the production guard.

This is a database/guard integration test. It does not instantiate WPF, authenticate signatures, execute complete Submit/Close workflows, or prove UI wiring beyond the identified production guard call sites. Unchanged-content checks for Submit/Close apply to the concurrency gate; existing workflow/status/authorization validation still applies.

Review validation: the current fixture passes 40/40 checks. On baseline `b4b3b0c` the same fixture passes 8/37 and fails 29 checks; absent guard support produces one aggregate lock failure instead of the four individual lock checks. The baseline investigation source files are identical at `e4d66337`.
