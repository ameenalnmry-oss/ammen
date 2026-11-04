# EM lifecycle regression

Run with .NET 8, a disposable-SQL-capable account in `EQ001_SQL_TEST_SETTINGS`,
and certificate trust configured for SQL Server:

```sh
dotnet run --project PharmaLIMS/tests/PharmaLIMS.EmLifecycleRegression -- "$PWD/PharmaLIMS"
```

The settings JSON supports `MasterConnectionString` or `server` and `password`.
The harness creates a uniquely named `EQ001_UAT_EM_*` database and drops it in
`finally`. It never changes an existing database. The reduced fixture uses the
production result numeric and observation-text storage types.

The linked production evidence helper runs real SQL to capture/fingerprint,
invalidate without rewriting history, reject cancelled plans, and check employee
identity against linked plan samples. Equipment-only reassignments and reverts must invalidate
closed decisions; fingerprints cover locked current usage and immutable instrument
assignment history, without consulting the live equipment master. Source boundary checks verify the actual
save/submit/review/approve/print methods call the shared guard and execute it
against the SQL fixture. The personnel grouping test invokes the production
key function and checks release SQL includes employee identity; it does not
instantiate WPF, perform a complete planning release, or spool a printer job.

To compare an unchanged baseline, export the required source files to a separate
directory and set `-p:SourceRoot=/absolute/baseline/PharmaLIMS`, passing the same
directory as the application-source argument. Missing new helper methods use the
former permissive behavior, allowing the behavioral assertions to fail at the
baseline without a compiler error. Use a separate `ArtifactsPath` for each run.
