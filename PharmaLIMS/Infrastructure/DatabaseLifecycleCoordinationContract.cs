namespace PharmaLIMS.Infrastructure
{
    internal static class DatabaseLifecycleCoordinationContract
    {
        internal const string ResourceName = "PharmaLIMS.SchemaMigration";

        // Transaction ownership is intentional. The Shared lock is released by
        // transaction rollback/disposal even if preflight is cancelled or fails.
        // This avoids the session-lock lifetime problems of earlier builds.
        internal const string AcquirePreflightSharedTransactionLockSql = @"
DECLARE @LockResult INT;
EXEC @LockResult=sys.sp_getapplock
    @Resource=N'PharmaLIMS.SchemaMigration',
    @LockMode=N'Shared',
    @LockOwner=N'Transaction',
    @LockTimeout=0;
SELECT @LockResult;";

        internal static string DescribeNegativeResult(int result)
        {
            return result switch
            {
                -1 => "The controlled database-maintenance lease is currently unavailable.",
                -2 => "The database lifecycle lock request was cancelled.",
                -3 => "The database lifecycle lock request was selected as a deadlock victim.",
                -999 => "The database lifecycle lock request was rejected because of a parameter or call error.",
                _ => "The database lifecycle lock request returned an unexpected negative result " + result + "."
            };
        }
    }
}
