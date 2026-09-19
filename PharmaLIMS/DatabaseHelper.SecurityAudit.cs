using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;

#nullable disable

namespace PharmaLIMS
{
    // Central permission gates, audit trail, electronic signatures, and sample workflow separation.
    public static partial class DatabaseHelper
    {
        private static string GetCurrentLoginUsername()
        {
            return string.IsNullOrWhiteSpace(Login.CurrentUser)
                ? string.Empty
                : Login.CurrentUser.Trim();
        }

        private static bool IsDevelopmentEnvironment() => AppConfig.IsDevelopment;

        private static bool IsAdministrativeRole(string role)
        {
            return role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                   role.Equals("Administrator", StringComparison.OrdinalIgnoreCase);
        }

        private static bool RoleIsOneOf(string role, params string[] allowedRoles)
        {
            if (string.IsNullOrWhiteSpace(role))
                return false;

            foreach (string allowedRole in allowedRoles)
            {
                if (role.Equals(allowedRole, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsDevelopmentAdministrator(string username)
        {
            if (!IsDevelopmentEnvironment() || !AppConfig.DevelopmentAdminFullPermissions)
                return false;

            string role = GetUserRole(username);
            return IsAdministrativeRole(role);
        }

        public static string GetUserRole(string username)
        {
            string cleanUsername = username == null ? "" : username.Trim();

            if (string.IsNullOrWhiteSpace(cleanUsername))
                cleanUsername = GetCurrentLoginUsername();

            if (string.IsNullOrWhiteSpace(cleanUsername))
                return "Unknown";

            try
            {
                DataTable rows = ExecuteQuery(@"
SELECT Role
FROM dbo.Users
WHERE Username = @UserIdentifier
  AND ISNULL(IsActive, 1) = 1
  AND ISNULL(MustChangePassword, 0) = 0
  AND NOT (ISNULL(IsLocked, 0) = 1 AND (LockedUntil IS NULL OR LockedUntil > SYSDATETIME()));",
                    new[]
                    {
                        new SqlParameter("@UserIdentifier", SqlDbType.NVarChar, 256)
                        {
                            Value = cleanUsername
                        }
                    });

                if (rows.Rows.Count != 1)
                    return "Unknown";

                string role = Convert.ToString(rows.Rows[0]["Role"], CultureInfo.InvariantCulture) ?? string.Empty;
                return string.IsNullOrWhiteSpace(role) ? "Unknown" : role.Trim();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error($"Unable to read the role for user '{cleanUsername}'.", ex);
                return "Unknown";
            }
        }

        private static bool GetUserPermissionFlag(string username, string permissionColumn, bool fallback)
        {
            if (string.IsNullOrWhiteSpace(permissionColumn) ||
                !AllowedPermissionColumns.Contains(permissionColumn))
                return fallback;

            if (!ColumnExists("Users", permissionColumn))
                return fallback;

            string cleanUsername = username == null ? "" : username.Trim();

            if (string.IsNullOrWhiteSpace(cleanUsername))
                cleanUsername = GetCurrentLoginUsername();

            if (string.IsNullOrWhiteSpace(cleanUsername))
                return fallback;

            try
            {
                string query = $@"
SELECT ISNULL([{permissionColumn}], 0) AS PermissionGranted
FROM dbo.Users
WHERE Username = @UserIdentifier
  AND ISNULL(IsActive, 1) = 1
  AND ISNULL(MustChangePassword, 0) = 0
  AND NOT (ISNULL(IsLocked, 0) = 1 AND (LockedUntil IS NULL OR LockedUntil > SYSDATETIME()));";

                DataTable rows = ExecuteQuery(query, new[]
                {
                    new SqlParameter("@UserIdentifier", SqlDbType.NVarChar, 256)
                    {
                        Value = cleanUsername
                    }
                });

                if (rows.Rows.Count != 1)
                    return fallback;

                object result = rows.Rows[0]["PermissionGranted"];
                return result == null || result == DBNull.Value
                    ? fallback
                    : Convert.ToBoolean(result, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error(
                    $"Unable to read permission '{permissionColumn}' for user '{cleanUsername}'.",
                    ex);
                return fallback;
            }
        }

        internal static string EnsureUserPermissionInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            string username,
            string permissionColumn,
            string actionDescription)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (string.IsNullOrWhiteSpace(permissionColumn) ||
                !AllowedPermissionColumns.Contains(permissionColumn))
            {
                throw new InvalidOperationException("The requested workflow permission is not recognized.");
            }

            string cleanUsername = string.IsNullOrWhiteSpace(username)
                ? GetCurrentLoginUsername()
                : username.Trim();

            if (string.IsNullOrWhiteSpace(cleanUsername))
                throw new UnauthorizedAccessException("An authenticated user is required for " + actionDescription + ".");

            string query = $@"
SELECT
       ISNULL(Role, N'') AS Role,
       ISNULL([{permissionColumn}], 0) AS PermissionGranted,
       ISNULL(IsActive, 1) AS IsActive,
       ISNULL(MustChangePassword, 0) AS MustChangePassword,
       CASE WHEN ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME())
            THEN 1 ELSE 0 END AS IsLockedNow
FROM dbo.Users WITH (UPDLOCK, HOLDLOCK)
WHERE Username = @UserIdentifier;";

            using SqlCommand command = new SqlCommand(query, connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@UserIdentifier", SqlDbType.NVarChar, 256).Value = cleanUsername;

            using SqlDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                throw new UnauthorizedAccessException("The authenticated user account was not found for " + actionDescription + ".");

            string role = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
            bool permissionGranted = !reader.IsDBNull(1) && Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture);
            bool isActive = reader.IsDBNull(2) || Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture);
            bool mustChangePassword = !reader.IsDBNull(3) && Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture);
            bool isLocked = Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture);

            if (reader.Read())
                throw new UnauthorizedAccessException("Duplicate user identity was detected. The action was blocked.");

            bool developmentAdministrator =
                AppConfig.IsDevelopment &&
                AppConfig.DevelopmentAdminFullPermissions &&
                IsAdministrativeRole(role);

            if (!isActive || isLocked || mustChangePassword || (!permissionGranted && !developmentAdministrator))
            {
                throw new UnauthorizedAccessException(
                    "The authenticated user does not have permission to " + actionDescription + ".");
            }

            return role;
        }

        internal static string EnsureQaApprovalAuthorizationInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            string username,
            string actionDescription)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (string.IsNullOrWhiteSpace(username))
                throw new UnauthorizedAccessException("An authenticated username is required for " + actionDescription + ".");

            using SqlCommand command = new SqlCommand(@"
SELECT ISNULL(Role, N''), ISNULL(CanApproveResults, 0), ISNULL(IsActive, 1), ISNULL(MustChangePassword,0),
       CASE WHEN ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME())
            THEN 1 ELSE 0 END
FROM dbo.Users WITH (UPDLOCK, HOLDLOCK)
WHERE Username = @Username;", connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@Username", SqlDbType.NVarChar, 256).Value = username.Trim();

            using SqlDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                throw new UnauthorizedAccessException("The authenticated user account was not found for " + actionDescription + ".");

            string role = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
            bool canApprove = !reader.IsDBNull(1) && Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture);
            bool isActive = reader.IsDBNull(2) || Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture);
            bool mustChangePassword = !reader.IsDBNull(3) && Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture);
            bool isLocked = Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture);

            if (reader.Read())
                throw new UnauthorizedAccessException("Duplicate user identity was detected. The action was blocked.");

            bool developmentAdministrator =
                AppConfig.IsDevelopment &&
                AppConfig.DevelopmentAdminFullPermissions &&
                IsAdministrativeRole(role);
            bool qaRole = RoleIsOneOf(role, "QA", "Quality Assurance");

            if (!isActive || isLocked || mustChangePassword || (!developmentAdministrator && (!qaRole || !canApprove)))
                throw new UnauthorizedAccessException("Only an active QA approver can " + actionDescription + ".");

            return role;
        }

        internal static string EnsureQaClosureAuthorizationInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            string username,
            string actionDescription)
        {
            return EnsureQaApprovalAuthorizationInTransaction(
                connection,
                transaction,
                username,
                actionDescription);
        }

        internal static string EnsureQualityEventManagementAuthorizationInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            string username,
            string actionDescription)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (string.IsNullOrWhiteSpace(username))
                throw new UnauthorizedAccessException("An authenticated username is required for " + actionDescription + ".");

            using SqlCommand command = new SqlCommand(@"
SELECT ISNULL(Role,N''),ISNULL(CanReviewResults,0),ISNULL(CanApproveResults,0),ISNULL(IsActive,1),ISNULL(MustChangePassword,0), CONVERT(bit,CASE WHEN ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()) THEN 1 ELSE 0 END) AS IsLockedNow
FROM dbo.Users WITH(UPDLOCK,HOLDLOCK)
WHERE Username=@Username;", connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@Username", SqlDbType.NVarChar, 256).Value = username.Trim();

            using SqlDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                throw new UnauthorizedAccessException("The authenticated user account was not found for " + actionDescription + ".");

            string role = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
            bool canReview = !reader.IsDBNull(1) && Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture);
            bool canApprove = !reader.IsDBNull(2) && Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture);
            bool isActive = reader.IsDBNull(3) || Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture);
            bool mustChangePassword = !reader.IsDBNull(4) && Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture);
            bool isLockedNow = Convert.ToBoolean(reader["IsLockedNow"], CultureInfo.InvariantCulture);
            if (reader.Read())
                throw new UnauthorizedAccessException("Duplicate user identity was detected. The action was blocked.");

            bool developmentAdministrator = AppConfig.IsDevelopment &&
                AppConfig.DevelopmentAdminFullPermissions && IsAdministrativeRole(role);
            if (!isActive || isLockedNow || mustChangePassword || (!developmentAdministrator && !canReview && !canApprove))
                throw new UnauthorizedAccessException("The authenticated user does not have permission to " + actionDescription + ".");

            return role;
        }

        internal static string EnsureActiveUserInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            string username,
            string actionDescription)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new UnauthorizedAccessException("An authenticated username is required for " + actionDescription + ".");

            using SqlCommand command = new SqlCommand(@"
SELECT ISNULL(IsActive, 1), ISNULL(Role, N''), ISNULL(MustChangePassword,0), CONVERT(bit,CASE WHEN ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()) THEN 1 ELSE 0 END) AS IsLockedNow
FROM dbo.Users WITH (UPDLOCK, HOLDLOCK)
WHERE Username = @Username;", connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@Username", SqlDbType.NVarChar, 256).Value = username.Trim();

            using SqlDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                throw new UnauthorizedAccessException("The authenticated user account was not found for " + actionDescription + ".");

            bool isActive = reader.IsDBNull(0) || Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture);
            string role = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
            bool mustChangePassword = !reader.IsDBNull(2) && Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture);
            bool isLockedNow = Convert.ToBoolean(reader["IsLockedNow"], CultureInfo.InvariantCulture);
            if (reader.Read())
                throw new UnauthorizedAccessException("Duplicate user identity was detected. The action was blocked.");
            if (!isActive || isLockedNow || mustChangePassword)
                throw new UnauthorizedAccessException("The authenticated user account is inactive, locked, or requires a password change.");

            return role;
        }

        public static bool CanCloseQualityEvent(string username)
        {
            if (IsDevelopmentAdministrator(username))
                return true;

            string role = GetUserRole(username);
            return RoleIsOneOf(role, "QA", "Quality Assurance") &&
                   GetUserPermissionFlag(username, "CanApproveResults", false);
        }

        public static bool CanEditResults(string username)
        {
            if (IsDevelopmentAdministrator(username))
                return true;

            return GetUserPermissionFlag(username, "CanEnterResults", false);
        }

        public static bool CanRegisterSamples(string username)
        {
            if (IsDevelopmentAdministrator(username))
                return true;

            return GetUserPermissionFlag(username, "CanRegisterSamples", false);
        }

        public static bool CanSubmitForReview(string username)
        {
            if (IsDevelopmentAdministrator(username))
                return true;

            return GetUserPermissionFlag(username, "CanEnterResults", false);
        }

        public static bool CanReviewResults(string username)
        {
            if (IsDevelopmentAdministrator(username))
                return true;

            return GetUserPermissionFlag(username, "CanReviewResults", false);
        }

        public static bool CanApproveResults(string username)
        {
            if (IsDevelopmentAdministrator(username))
                return true;

            return GetUserPermissionFlag(username, "CanApproveResults", false);
        }

        public static bool CanIssueCertificate(string username)
        {
            if (IsDevelopmentAdministrator(username))
                return true;

            return GetUserPermissionFlag(username, "CanIssueCOA", false);
        }

        public static bool CanCancelCertificate(string username)
        {
            if (IsDevelopmentAdministrator(username))
                return true;

            return GetUserPermissionFlag(username, "CanCancelCOA", false);
        }

        public static bool CanAccessReports(string username)
        {
            if (IsDevelopmentAdministrator(username))
                return true;

            return GetUserPermissionFlag(username, "CanAccessReports", false);
        }

        public static bool CanManageUsers(string username)
        {
            if (IsDevelopmentAdministrator(username))
                return true;

            return GetUserPermissionFlag(username, "CanManageUsers", false);
        }

        public static bool CanManageSettings(string username)
        {
            if (IsDevelopmentAdministrator(username))
                return true;

            return GetUserPermissionFlag(username, "CanManageSettings", false);
        }

        public static string GetOldValue(string tableName, int recordId, string columnName)
        {
            if (recordId <= 0 || string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(columnName))
                return string.Empty;

            string safeTableName;
            string safeColumnName;
            string idColumn;

            if (tableName.Equals("EM_EventPlates", StringComparison.OrdinalIgnoreCase) &&
                (columnName.Equals("ResultCFU", StringComparison.OrdinalIgnoreCase) ||
                 columnName.Equals("Status", StringComparison.OrdinalIgnoreCase)))
            {
                safeTableName = "EM_EventPlates";
                safeColumnName = columnName.Equals("ResultCFU", StringComparison.OrdinalIgnoreCase)
                    ? "ResultCFU"
                    : "Status";
                idColumn = "Id";
            }
            else if (tableName.Equals("EM_Events", StringComparison.OrdinalIgnoreCase) &&
                     columnName.Equals("FinalResult", StringComparison.OrdinalIgnoreCase))
            {
                safeTableName = "EM_Events";
                safeColumnName = "FinalResult";
                idColumn = "Id";
            }
            else
            {
                throw new ArgumentException("The requested audit value source is not allow-listed.");
            }

            string query = $"SELECT TOP 1 [{safeColumnName}] FROM [{safeTableName}] WHERE [{idColumn}] = @recordId";

            SqlParameter[] pars =
            {
                new SqlParameter("@recordId", recordId)
            };

            object result = ExecuteScalar(query, pars);
            return result == null || result == DBNull.Value ? string.Empty : result.ToString();
        }

        private static void RequireComplianceTable(string tableName, string purpose)
        {
            if (!TableExists(tableName))
            {
                throw new InvalidOperationException(
                    $"Required GMP compliance table dbo.{tableName} is missing. Install the database update script before performing {purpose}.");
            }
        }

        private static SqlParameter NVarCharParameter(string name, string value, int size)
        {
            return new SqlParameter(name, SqlDbType.NVarChar, size)
            {
                Value = string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value.Trim()
            };
        }

        private static SqlParameter NVarCharMaxParameter(string name, string value)
        {
            return new SqlParameter(name, SqlDbType.NVarChar, -1)
            {
                Value = string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value.Trim()
            };
        }

        public static int AddAuditTrailAdvanced(
            string tableName,
            int recordId,
            string action,
            string oldValue,
            string newValue,
            string reason,
            string performedBy)
        {
            return AddAuditTrailAdvanced(
                tableName,
                recordId,
                action,
                oldValue,
                newValue,
                reason,
                performedBy,
                null,
                null,
                null,
                null);
        }

        public static int AddAuditTrailAdvanced(
            string tableName,
            int recordId,
            string action,
            string oldValue,
            string newValue,
            string reason,
            string performedBy,
            string fieldName,
            string testName,
            string sampleNumber,
            string moduleName)
        {
            if (!TableExists("AuditTrail"))
            {
                if (AppConfig.EnforceAuditTrail)
                    RequireComplianceTable("AuditTrail", "audit-tracked data changes");

                return 0;
            }

            string userName = ResolveAuthenticatedSigner(performedBy);
            string workstation = Environment.MachineName;

            List<string> columns = new List<string>();
            List<string> values = new List<string>();
            List<SqlParameter> pars = new List<SqlParameter>();

            void AddColumn(string columnName, string parameterName, SqlDbType dbType, object value, int size = 0)
            {
                if (!ColumnExists("AuditTrail", columnName))
                    return;

                columns.Add(columnName);
                values.Add(parameterName);

                SqlParameter parameter = size != 0
                    ? new SqlParameter(parameterName, dbType, size)
                    : new SqlParameter(parameterName, dbType);

                parameter.Value = value ?? DBNull.Value;
                pars.Add(parameter);
            }

            object Clean(string value)
            {
                return string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value.Trim();
            }

            AddColumn("TableName", "@tableName", SqlDbType.NVarChar, Clean(tableName), 128);
            AddColumn("RecordID", "@recordId", SqlDbType.Int, recordId);
            AddColumn("Action", "@action", SqlDbType.NVarChar, Clean(action), 200);
            AddColumn("FieldName", "@fieldName", SqlDbType.NVarChar, Clean(fieldName), 200);
            AddColumn("TestName", "@testName", SqlDbType.NVarChar, Clean(testName), 300);
            AddColumn("SampleNumber", "@sampleNumber", SqlDbType.NVarChar, Clean(sampleNumber), 100);
            AddColumn("ModuleName", "@moduleName", SqlDbType.NVarChar, Clean(moduleName), 100);
            AddColumn("OldValue", "@oldValue", SqlDbType.NVarChar, Clean(oldValue), -1);
            AddColumn("NewValue", "@newValue", SqlDbType.NVarChar, Clean(newValue), -1);
            AddColumn("Reason", "@reason", SqlDbType.NVarChar, Clean(reason), -1);
            AddColumn("ChangedBy", "@changedBy", SqlDbType.NVarChar, Clean(userName), 100);
            AddColumn("PerformedBy", "@performedBy", SqlDbType.NVarChar, Clean(userName), 100);
            AddColumn("IPAddress", "@ipAddress", SqlDbType.NVarChar, DBNull.Value, 100);
            AddColumn("SourceWorkstation", "@sourceWorkstation", SqlDbType.NVarChar, Clean(workstation), 200);
            AddColumn("ComputerName", "@computerName", SqlDbType.NVarChar, Clean(workstation), 200);
            AddColumn("SourceApplication", "@sourceApplication", SqlDbType.NVarChar, "PharmaLIMS", 100);

            if (ColumnExists("AuditTrail", "ChangeDate"))
            {
                columns.Add("ChangeDate");
                values.Add("GETDATE()");
            }

            if (ColumnExists("AuditTrail", "CreatedUtc"))
            {
                columns.Add("CreatedUtc");
                values.Add("SYSUTCDATETIME()");
            }

            if (ColumnExists("AuditTrail", "UtcRecordedAt"))
            {
                columns.Add("UtcRecordedAt");
                values.Add("SYSUTCDATETIME()");
            }

            if (columns.Count == 0)
                return 0;

            string query = $@"
                INSERT INTO AuditTrail
                (
                    {string.Join(",\n                    ", columns)}
                )
                VALUES
                (
                    {string.Join(",\n                    ", values)}
                )";

            return ExecuteNonQuery(query, pars.ToArray());
        }


        public static int AddAuditTrailAdvanced(
            SqlConnection connection,
            SqlTransaction transaction,
            string tableName,
            int recordId,
            string action,
            string oldValue,
            string newValue,
            string reason,
            string performedBy,
            string fieldName = null,
            string testName = null,
            string sampleNumber = null,
            string moduleName = null)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            using (SqlCommand exists = new SqlCommand(
                "SELECT CASE WHEN OBJECT_ID(N'dbo.AuditTrail',N'U') IS NULL THEN 0 ELSE 1 END;",
                connection,
                transaction))
            {
                if (Convert.ToInt32(exists.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                {
                    if (AppConfig.EnforceAuditTrail)
                        throw new InvalidOperationException("Required GMP compliance table dbo.AuditTrail is missing.");
                    return 0;
                }
            }

            string effectiveUser = ResolveAuthenticatedSigner(performedBy);
            string workstation = Environment.MachineName;
            var requested = new (string Column, string Parameter, SqlDbType Type, int Size, object Value)[]
            {
                ("TableName", "@tableName", SqlDbType.NVarChar, 128, DbText(tableName)),
                ("RecordID", "@recordId", SqlDbType.Int, 0, recordId),
                ("Action", "@action", SqlDbType.NVarChar, 200, DbText(action)),
                ("FieldName", "@fieldName", SqlDbType.NVarChar, 200, DbText(fieldName)),
                ("TestName", "@testName", SqlDbType.NVarChar, 300, DbText(testName)),
                ("SampleNumber", "@sampleNumber", SqlDbType.NVarChar, 100, DbText(sampleNumber)),
                ("ModuleName", "@moduleName", SqlDbType.NVarChar, 100, DbText(moduleName)),
                ("OldValue", "@oldValue", SqlDbType.NVarChar, -1, DbText(oldValue)),
                ("NewValue", "@newValue", SqlDbType.NVarChar, -1, DbText(newValue)),
                ("Reason", "@reason", SqlDbType.NVarChar, -1, DbText(reason)),
                ("ChangedBy", "@changedBy", SqlDbType.NVarChar, 100, DbText(effectiveUser)),
                ("PerformedBy", "@performedBy", SqlDbType.NVarChar, 100, DbText(effectiveUser)),
                ("SourceWorkstation", "@sourceWorkstation", SqlDbType.NVarChar, 200, DbText(workstation)),
                ("ComputerName", "@computerName", SqlDbType.NVarChar, 200, DbText(workstation)),
                ("SourceApplication", "@sourceApplication", SqlDbType.NVarChar, 100, "PharmaLIMS")
            };

            var columns = new List<string>();
            var values = new List<string>();
            var parameters = new List<SqlParameter>();

            foreach (var item in requested)
            {
                using SqlCommand columnCommand = new SqlCommand(@"
SELECT CASE WHEN COL_LENGTH(N'dbo.AuditTrail',@ColumnName) IS NULL THEN 0 ELSE 1 END;", connection, transaction);
                columnCommand.Parameters.Add("@ColumnName", SqlDbType.NVarChar, 128).Value = item.Column;
                if (Convert.ToInt32(columnCommand.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                    continue;

                columns.Add(item.Column);
                values.Add(item.Parameter);
                SqlParameter parameter = item.Size == 0
                    ? new SqlParameter(item.Parameter, item.Type)
                    : new SqlParameter(item.Parameter, item.Type, item.Size);
                parameter.Value = item.Value ?? DBNull.Value;
                parameters.Add(parameter);
            }

            AddSqlTimestampColumn(connection, transaction, "ChangeDate", "GETDATE()", columns, values);
            AddSqlTimestampColumn(connection, transaction, "CreatedUtc", "SYSUTCDATETIME()", columns, values);
            AddSqlTimestampColumn(connection, transaction, "UtcRecordedAt", "SYSUTCDATETIME()", columns, values);

            if (columns.Count == 0)
                return 0;

            using SqlCommand insert = new SqlCommand($@"
INSERT dbo.AuditTrail ({string.Join(",", columns)})
VALUES ({string.Join(",", values)});", connection, transaction);
            insert.Parameters.AddRange(parameters.ToArray());
            return insert.ExecuteNonQuery();
        }

        private static object DbText(string value) =>
            string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value.Trim();

        private static void AddSqlTimestampColumn(
            SqlConnection connection,
            SqlTransaction transaction,
            string columnName,
            string sqlExpression,
            List<string> columns,
            List<string> values)
        {
            using SqlCommand command = new SqlCommand(@"
SELECT CASE WHEN COL_LENGTH(N'dbo.AuditTrail',@ColumnName) IS NULL THEN 0 ELSE 1 END;", connection, transaction);
            command.Parameters.Add("@ColumnName", SqlDbType.NVarChar, 128).Value = columnName;
            if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
            {
                columns.Add(columnName);
                values.Add(sqlExpression);
            }
        }

        private static string NormalizeWorkflowUsername(string username)
        {
            return (username ?? "").Trim();
        }

        private static string SqlInParameters(string baseName, int count)
        {
            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append("@").Append(baseName).Append(i.ToString());
            }

            return builder.ToString();
        }

        public static bool HasUserSignedSampleAction(
            int sampleId,
            string username,
            IEnumerable<string> actionTypes,
            out string matchedAction,
            out string matchedAt)
        {
            matchedAction = "";
            matchedAt = "";

            if (!TableExists("ElectronicSignatures"))
                return false;

            string cleanUser = NormalizeWorkflowUsername(username);
            if (string.IsNullOrWhiteSpace(cleanUser))
                return false;

            List<string> actions = new List<string>();
            if (actionTypes != null)
            {
                foreach (string action in actionTypes)
                {
                    if (!string.IsNullOrWhiteSpace(action))
                        actions.Add(action.Trim());
                }
            }

            if (actions.Count == 0)
                return false;

            string inClause = SqlInParameters("action", actions.Count);

            string query = @"
                SELECT TOP 1
                    ActionType,
                    SignedAt
                FROM dbo.ElectronicSignatures
                WHERE SampleID = @sampleId
                  AND UPPER(LTRIM(RTRIM(ISNULL(SignedBy, '')))) = UPPER(LTRIM(RTRIM(@username)))
                  AND ActionType IN (" + inClause + @")
                ORDER BY SignedAt DESC";

            List<SqlParameter> pars = new List<SqlParameter>
            {
                new SqlParameter("@sampleId", SqlDbType.Int) { Value = sampleId },
                new SqlParameter("@username", SqlDbType.NVarChar, 100) { Value = cleanUser }
            };

            for (int i = 0; i < actions.Count; i++)
                pars.Add(new SqlParameter("@action" + i.ToString(), SqlDbType.NVarChar, 100) { Value = actions[i] });

            DataTable table = ExecuteQuery(query, pars.ToArray());

            if (table.Rows.Count == 0)
                return false;

            matchedAction = table.Rows[0].GetSafeString("ActionType");
            object signedAtObj = table.Rows[0]["SignedAt"];
            matchedAt = signedAtObj == DBNull.Value ? "" : Convert.ToDateTime(signedAtObj).ToString("yyyy-MM-dd HH:mm");
            return true;
        }

        internal static void EnsureSampleWorkflowSeparationInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId,
            string username,
            string userRole,
            string requestedAction)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (sampleId <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleId));

            string cleanAction = (requestedAction ?? string.Empty).Trim();
            string cleanUser = NormalizeWorkflowUsername(username);
            string cleanRole = (userRole ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(cleanUser) || string.IsNullOrWhiteSpace(cleanAction))
                throw new UnauthorizedAccessException("An authenticated user and workflow action are required for separation-of-duties validation.");

            bool developmentAdministrator =
                AppConfig.IsDevelopment &&
                AppConfig.DevelopmentAdminFullPermissions &&
                IsAdministrativeRole(cleanRole);

            if (developmentAdministrator)
                return;

            string[] resultEntryActions =
            {
                "Result Entry",
                "Results Entry",
                "Enter Results",
                "Entered Results"
            };

            string[] submitReviewActions =
            {
                "Submit Review",
                "Submit for Review",
                "Submitted for Review"
            };

            string[] reviewActions =
            {
                "Review",
                "Review Sample",
                "Reviewed"
            };

            List<string> conflictingActions = new List<string>();

            if (cleanAction.Equals("Review", StringComparison.OrdinalIgnoreCase) ||
                cleanAction.Equals("Review Sample", StringComparison.OrdinalIgnoreCase))
            {
                conflictingActions.AddRange(resultEntryActions);
                conflictingActions.AddRange(submitReviewActions);
            }
            else if (cleanAction.Equals("Approve", StringComparison.OrdinalIgnoreCase) ||
                     cleanAction.Equals("Approve Sample", StringComparison.OrdinalIgnoreCase) ||
                     cleanAction.Equals("Approval", StringComparison.OrdinalIgnoreCase))
            {
                conflictingActions.AddRange(resultEntryActions);
                conflictingActions.AddRange(reviewActions);
            }
            else if (cleanAction.Equals("COA Issuance", StringComparison.OrdinalIgnoreCase) ||
                     cleanAction.Equals("Issue Certificate", StringComparison.OrdinalIgnoreCase) ||
                     cleanAction.Equals("Certificate Issuance", StringComparison.OrdinalIgnoreCase))
            {
                conflictingActions.AddRange(resultEntryActions);
                conflictingActions.AddRange(submitReviewActions);
                conflictingActions.AddRange(reviewActions);
            }
            else
            {
                return;
            }

            string inClause = SqlInParameters("workflowAction", conflictingActions.Count);
            string sql = @"
SELECT TOP (1)
       ActionType,
       SignedAt
FROM dbo.ElectronicSignatures WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID = @SampleID
  AND UPPER(LTRIM(RTRIM(ISNULL(SignedBy,N'')))) = UPPER(LTRIM(RTRIM(@Username)))
  AND ActionType IN (" + inClause + @")
ORDER BY SignedAt DESC;";

            using SqlCommand command = new SqlCommand(sql, connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
            command.Parameters.Add("@Username", SqlDbType.NVarChar, 100).Value = cleanUser;

            for (int i = 0; i < conflictingActions.Count; i++)
            {
                command.Parameters.Add("@workflowAction" + i.ToString(CultureInfo.InvariantCulture), SqlDbType.NVarChar, 100).Value =
                    conflictingActions[i];
            }

            using SqlDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return;

            string matchedAction = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
            string matchedAt = reader.IsDBNull(1)
                ? string.Empty
                : reader.GetDateTime(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            string whenText = string.IsNullOrWhiteSpace(matchedAt) ? string.Empty : " at " + matchedAt;
            throw new InvalidOperationException(
                "Workflow separation violation. The same user cannot perform independent GMP workflow steps for the same sample. " +
                "User: " + cleanUser + "; Previous action: " + matchedAction + whenText + "; Requested action: " + cleanAction + ".");
        }

        public static bool ValidateSampleWorkflowSeparation(
            int sampleId,
            string username,
            string requestedAction,
            out string message)
        {
            message = "";

            string cleanAction = (requestedAction ?? "").Trim();
            string cleanUser = NormalizeWorkflowUsername(username);

            if (sampleId <= 0 || string.IsNullOrWhiteSpace(cleanUser) || string.IsNullOrWhiteSpace(cleanAction))
                return true;

            // Development bypass: Admin/Administrator can perform all workflow steps while AppConfig.IsProduction = false.
            // Do not enable this bypass in production GMP use.
            if (IsDevelopmentAdministrator(cleanUser))
                return true;

            string[] resultEntryActions =
            {
                "Result Entry",
                "Results Entry",
                "Enter Results",
                "Entered Results"
            };

            string[] submitReviewActions =
            {
                "Submit Review",
                "Submit for Review",
                "Submitted for Review"
            };

            string[] reviewActions =
            {
                "Review",
                "Review Sample",
                "Reviewed"
            };

            string[] approvalActions =
            {
                "Approve",
                "Approve Sample",
                "Approval",
                "Approved"
            };

            List<string> conflictingActions = new List<string>();

            if (cleanAction.Equals("Review", StringComparison.OrdinalIgnoreCase) ||
                cleanAction.Equals("Review Sample", StringComparison.OrdinalIgnoreCase))
            {
                conflictingActions.AddRange(resultEntryActions);
                conflictingActions.AddRange(submitReviewActions);
            }
            else if (cleanAction.Equals("Approve", StringComparison.OrdinalIgnoreCase) ||
                     cleanAction.Equals("Approve Sample", StringComparison.OrdinalIgnoreCase) ||
                     cleanAction.Equals("Approval", StringComparison.OrdinalIgnoreCase))
            {
                conflictingActions.AddRange(resultEntryActions);
                conflictingActions.AddRange(reviewActions);
            }
            else if (cleanAction.Equals("COA Issuance", StringComparison.OrdinalIgnoreCase) ||
                     cleanAction.Equals("Issue Certificate", StringComparison.OrdinalIgnoreCase) ||
                     cleanAction.Equals("Certificate Issuance", StringComparison.OrdinalIgnoreCase))
            {
                // COA issuance is allowed for the same QA user who approved the sample.
                // It remains blocked for the analyst/result-entry user and the reviewer.
                conflictingActions.AddRange(resultEntryActions);
                conflictingActions.AddRange(submitReviewActions);
                conflictingActions.AddRange(reviewActions);
            }
            else
            {
                return true;
            }

            if (HasUserSignedSampleAction(sampleId, cleanUser, conflictingActions, out string matchedAction, out string matchedAt))
            {
                string whenText = string.IsNullOrWhiteSpace(matchedAt) ? "" : " at " + matchedAt;
                message =
                    "Workflow separation violation.\n\n" +
                    "The same user cannot perform independent GMP workflow steps for the same sample.\n\n" +
                    "User: " + cleanUser + "\n" +
                    "Previous action: " + matchedAction + whenText + "\n" +
                    "Requested action: " + cleanAction + "\n\n" +
                    "Use a different authorized user for this step.";

                return false;
            }

            return true;
        }

        private static string ResolveAuthenticatedSigner(string requestedSigner)
        {
            string activeAccount = string.IsNullOrWhiteSpace(Login.CurrentUser)
                ? string.Empty
                : Login.CurrentUser.Trim();

            if (!string.IsNullOrWhiteSpace(activeAccount))
                return activeAccount;

            if (AppConfig.EnforceElectronicSignatureStorage || AppConfig.EnforceAuditTrail)
            {
                throw new InvalidOperationException(
                    "An authenticated user account is required before a regulated signature or audit record can be stored.");
            }

            return string.IsNullOrWhiteSpace(requestedSigner)
                ? "Unknown User"
                : requestedSigner.Trim();
        }

        private static void ValidateSignatureMetadata(int recordId, string actionType, string meaningOfSignature)
        {
            if (recordId <= 0)
                throw new ArgumentOutOfRangeException(nameof(recordId), "A valid regulated record identifier is required.");

            if (string.IsNullOrWhiteSpace(actionType))
                throw new ArgumentException("The electronic-signature action is required.", nameof(actionType));

            if (string.IsNullOrWhiteSpace(meaningOfSignature))
                throw new ArgumentException("The meaning of the electronic signature is required.", nameof(meaningOfSignature));
        }

        public static int AddElectronicSignature(
            int sampleId,
            string actionType,
            string signedBy,
            string meaningOfSignature,
            string reason = null)
        {
            ValidateSignatureMetadata(sampleId, actionType, meaningOfSignature);

            if (!TableExists("ElectronicSignatures"))
            {
                if (AppConfig.EnforceElectronicSignatureStorage)
                    RequireComplianceTable("ElectronicSignatures", "electronically signed actions");

                return 0;
            }

            string effectiveSignedBy = ResolveAuthenticatedSigner(signedBy);

            string role = GetUserRole(effectiveSignedBy);

            if (role.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(Login.CurrentUser))
            {
                role = GetUserRole(Login.CurrentUser);
            }

            string query = @"
                INSERT INTO ElectronicSignatures
                (
                    SampleID,
                    ActionType,
                    ActionReason,
                    SignedBy,
                    MeaningOfSignature,
                    UserRole,
                    SignedAt
                )
                VALUES
                (
                    @sampleId,
                    @actionType,
                    @reason,
                    @signedBy,
                    @meaning,
                    @role,
                    GETDATE()
                )";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleId", SqlDbType.Int) { Value = sampleId },
                NVarCharParameter("@actionType", actionType, 100),
                NVarCharMaxParameter("@reason", reason),
                NVarCharParameter("@signedBy", effectiveSignedBy, 100),
                NVarCharParameter("@meaning", meaningOfSignature, 255),
                NVarCharParameter("@role", role, 100)
            };

            return ExecuteNonQuery(query, pars);
        }

        public static string StartAnalysis(int sampleId, string startedBy)
        {
            startedBy = ResolveAuthenticatedSigner(startedBy);
            string query = "EXEC sp_StartAnalysis @SampleID, @StartedBy";

            SqlParameter[] pars =
            {
                new SqlParameter("@SampleID", sampleId),
                new SqlParameter("@StartedBy", startedBy)
            };

            DataTable dt = ExecuteQuery(query, pars);
            return dt.Rows.Count > 0 ? dt.Rows[0]["Message"].ToString() ?? "Error" : "Error";
        }

        public static string ReviewSample(int sampleId, string reviewedBy, string comments)
        {
            reviewedBy = ResolveAuthenticatedSigner(reviewedBy);
            string query = "EXEC sp_ReviewSample @SampleID, @ReviewedBy, @Comments";

            SqlParameter[] pars =
            {
                new SqlParameter("@SampleID", sampleId),
                new SqlParameter("@ReviewedBy", reviewedBy),
                new SqlParameter("@Comments", string.IsNullOrWhiteSpace(comments) ? (object)DBNull.Value : comments)
            };

            DataTable dt = ExecuteQuery(query, pars);
            return dt.Rows.Count > 0 ? dt.Rows[0]["Message"].ToString() ?? "Error" : "Error";
        }

        public static string ApproveSample(int sampleId, string approvedBy, string signature, string meaningOfSignature)
        {
            approvedBy = ResolveAuthenticatedSigner(approvedBy);
            string query = "EXEC sp_ApproveSample @SampleID, @ApprovedBy, @Signature, @MeaningOfSignature";

            SqlParameter[] pars =
            {
                new SqlParameter("@SampleID", sampleId),
                new SqlParameter("@ApprovedBy", approvedBy),
                new SqlParameter("@Signature", signature),
                new SqlParameter("@MeaningOfSignature", meaningOfSignature)
            };

            DataTable dt = ExecuteQuery(query, pars);
            return dt.Rows.Count > 0 ? dt.Rows[0]["Message"].ToString() ?? "Error" : "Error";
        }


    }
}
