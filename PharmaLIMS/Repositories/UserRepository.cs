using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Interfaces;
using PharmaLIMS.Models;
using PharmaLIMS.Services;
using System.Data;
using System.Threading;

namespace PharmaLIMS.Repositories
{
    public class UserRepository : IRepository<User, int>
    {
        private static readonly HashSet<string> AllowedPermissionColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "CanAccessWater",
            "CanAccessEM",
            "CanRegisterSamples",
            "CanEnterResults",
            "CanReviewResults",
            "CanApproveResults",
            "CanIssueCOA",
            "CanCancelCOA",
            "CanAccessReports",
            "CanManageUsers",
            "CanManageSettings"
        };

        private readonly DatabaseConnection _connection;

        public UserRepository(DatabaseConnection connection)
        {
            _connection = connection;
        }

        public async Task<User?> GetByIdAsync(int id)
        {
            const string query = @"SELECT UserID, Username, FullName, Role, Department, Section,
                    PasswordHash, PasswordHashNew, PasswordSalt, AuthenticationRowVersion,
                    IsActive, LastLogin, FailedLoginAttempts, IsLocked, LockedUntil,
                    MustChangePassword, PasswordChangedAt,
                    CanAccessWater, CanAccessEM, CanRegisterSamples, CanEnterResults,
                    CanReviewResults, CanApproveResults, CanIssueCOA, CanCancelCOA,
                    CanAccessReports, CanManageUsers, CanManageSettings FROM Users WHERE UserID = @UserId";
            var parameters = new[] { new SqlParameter("@UserId", id) };

            var dt = await _connection.ExecuteQueryAsync(query, parameters);
            return dt.Rows.Count > 0 ? MapToUser(dt.Rows[0]) : null;
        }

        public async Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default)
        {
            const string query = @"SELECT UserID, Username, FullName, Role, Department, Section,
                    PasswordHash, PasswordHashNew, PasswordSalt, AuthenticationRowVersion,
                    IsActive, LastLogin, FailedLoginAttempts,
                    CONVERT(bit, CASE
                        WHEN ISNULL(IsLocked, 0) = 1
                         AND (LockedUntil IS NULL OR LockedUntil > SYSDATETIME()) THEN 1
                        ELSE 0
                    END) AS IsLocked,
                    LockedUntil,
                    MustChangePassword, PasswordChangedAt,
                    CanAccessWater, CanAccessEM, CanRegisterSamples, CanEnterResults,
                    CanReviewResults, CanApproveResults, CanIssueCOA, CanCancelCOA,
                    CanAccessReports, CanManageUsers, CanManageSettings FROM Users WHERE Username = @Username";
            var parameters = new[] { new SqlParameter("@Username", username) };

            var dt = await _connection.ExecuteQueryAsync(query, parameters, commandTimeoutSeconds: 10, cancellationToken: cancellationToken);
            if (dt.Rows.Count > 1)
                throw new InvalidOperationException("Duplicate usernames were detected. Authentication is blocked until user identity is reconciled.");
            return dt.Rows.Count == 1 ? MapToUser(dt.Rows[0]) : null;
        }

        public async Task<IEnumerable<User>> GetAllAsync()
        {
            const string query = @"SELECT UserID, Username, FullName, Role, Department, Section,
                    IsActive, LastLogin, FailedLoginAttempts,
                    CONVERT(bit, CASE
                        WHEN ISNULL(IsLocked, 0) = 1
                         AND (LockedUntil IS NULL OR LockedUntil > SYSDATETIME()) THEN 1
                        ELSE 0
                    END) AS IsLocked,
                    LockedUntil,
                    MustChangePassword, PasswordChangedAt, AuthenticationRowVersion,
                    CanAccessWater, CanAccessEM, CanRegisterSamples, CanEnterResults,
                    CanReviewResults, CanApproveResults, CanIssueCOA, CanCancelCOA,
                    CanAccessReports, CanManageUsers, CanManageSettings FROM Users ORDER BY Username";
            var dt = await _connection.ExecuteQueryAsync(query);
            var users = new List<User>();

            foreach (DataRow row in dt.Rows)
            {
                var user = MapToUser(row);
                user.ClearSensitivePasswordData();
                users.Add(user);
            }

            return users;
        }

        [Obsolete("Direct user mutations are prohibited. Use UserAdministrationService so authorization, rowversion, electronic signature, and audit controls cannot be bypassed.", error: true)]
        public Task<int> AddAsync(User entity) =>
            throw new NotSupportedException(
                "Direct user creation through UserRepository is disabled. Use UserAdministrationService.");

        [Obsolete("Direct user mutations are prohibited. Use UserAdministrationService so authorization, rowversion, electronic signature, and audit controls cannot be bypassed.", error: true)]
        public Task<int> UpdateAsync(User entity) =>
            throw new NotSupportedException(
                "Direct user updates through UserRepository are disabled. Use UserAdministrationService.");

        [Obsolete("Direct user mutations are prohibited. Use UserAdministrationService so authorization, rowversion, electronic signature, and audit controls cannot be bypassed.", error: true)]
        public Task<int> DeleteAsync(int id) =>
            throw new NotSupportedException(
                "Direct user deactivation through UserRepository is disabled. Use UserAdministrationService.");

        public Task<bool> RecordSuccessfulLoginAsync(
            User verifiedUser, string? upgradedHash, string? upgradedSalt, CancellationToken cancellationToken = default) =>
            CompleteAuthenticationAsync(verifiedUser, true, upgradedHash, upgradedSalt, cancellationToken);

        public Task<bool> ResetAuthenticationFailuresAsync(
            User verifiedUser, string? upgradedHash, string? upgradedSalt, CancellationToken cancellationToken = default) =>
            CompleteAuthenticationAsync(verifiedUser, false, upgradedHash, upgradedSalt, cancellationToken);

        private async Task<bool> CompleteAuthenticationAsync(
            User verifiedUser, bool isLogin, string? upgradedHash, string? upgradedSalt,
            CancellationToken cancellationToken)
        {
            if (verifiedUser == null || verifiedUser.AuthenticationRowVersion.Length != 8)
                return false;
            bool upgrade = upgradedHash != null || upgradedSalt != null;
            if (upgrade && (string.IsNullOrWhiteSpace(upgradedHash) || string.IsNullOrWhiteSpace(upgradedSalt)))
                throw new ArgumentException("A credential upgrade requires both a hash and salt.");

            var parameters = new[]
            {
                new SqlParameter("@UserID", SqlDbType.Int) { Value = verifiedUser.UserId },
                new SqlParameter("@Username", SqlDbType.NVarChar, 256) { Value = verifiedUser.Username },
                new SqlParameter("@ExpectedVersion", SqlDbType.Binary, 8) { Value = verifiedUser.AuthenticationRowVersion },
                new SqlParameter("@IsLogin", SqlDbType.Bit) { Value = isLogin },
                new SqlParameter("@Upgrade", SqlDbType.Bit) { Value = upgrade },
                new SqlParameter("@PasswordHashNew", SqlDbType.NVarChar, 512) { Value = (object?)upgradedHash ?? DBNull.Value },
                new SqlParameter("@PasswordSalt", SqlDbType.NVarChar, 256) { Value = (object?)upgradedSalt ?? DBNull.Value }
            };
            DataTable committed = await _connection.ExecuteQueryAsync(
                AuthenticationCommitContract.Sql, parameters,
                commandTimeoutSeconds: 10, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (committed.Rows.Count != 1) return false;
            verifiedUser.AuthenticationRowVersion = (byte[])committed.Rows[0]["AuthenticationRowVersion"];
            return true;
        }

        public async Task<int> RegisterAuthenticationFailureAsync(
            User verifiedUser, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(verifiedUser);
            if (verifiedUser.UserId <= 0 || string.IsNullOrWhiteSpace(verifiedUser.Username))
                throw new ArgumentException("A checked account identity is required.", nameof(verifiedUser));

            // Keep exact credential strings: hashes and salts are case-sensitive
            // evidence even when the database uses a case-insensitive collation.
            var parameters = new[]
            {
                new SqlParameter("@VerifiedUserID", SqlDbType.Int) { Value = verifiedUser.UserId },
                new SqlParameter("@Username", SqlDbType.NVarChar, 256) { Value = verifiedUser.Username },
                new SqlParameter("@VerifiedLegacyHash", SqlDbType.NVarChar, -1) { Value = verifiedUser.PasswordHash ?? string.Empty },
                new SqlParameter("@VerifiedHash", SqlDbType.NVarChar, -1) { Value = verifiedUser.PasswordHashNew ?? string.Empty },
                new SqlParameter("@VerifiedSalt", SqlDbType.NVarChar, -1) { Value = verifiedUser.PasswordSalt ?? string.Empty }
            };
            return await _connection.ExecuteNonQueryAsync(
                AuthenticationCommitContract.FailureSql, parameters,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> ChangeVerifiedPasswordAsync(
            User verifiedUser,
            string newPasswordHash,
            string newPasswordSalt,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(verifiedUser);
            if (verifiedUser.UserId <= 0 || string.IsNullOrWhiteSpace(verifiedUser.Username) ||
                verifiedUser.AuthenticationRowVersion.Length != 8)
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(newPasswordHash) || string.IsNullOrWhiteSpace(newPasswordSalt))
                throw new ArgumentException("A complete new password credential is required.");

            await using SqlConnection connection = _connection.CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqlTransaction transaction =
                (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                using SqlCommand command = new SqlCommand(@"
UPDATE dbo.Users
SET PasswordHash = N'[MIGRATED]',
    PasswordHashNew = @PasswordHashNew,
    PasswordSalt = @PasswordSalt,
    MustChangePassword = 0,
    PasswordChangedAt = SYSUTCDATETIME(),
    FailedLoginAttempts = 0,
    IsLocked = 0,
    LockedUntil = NULL,
    UpdatedAt = SYSUTCDATETIME()
OUTPUT inserted.AuthenticationRowVersion, inserted.PasswordChangedAt
WHERE UserID = @UserID
  AND Username = @Username
  AND AuthenticationRowVersion = @ExpectedVersion
  AND ISNULL(IsActive,1)=1
  AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()));", connection, transaction);
                command.Parameters.Add("@UserID", SqlDbType.Int).Value = verifiedUser.UserId;
                command.Parameters.Add("@Username", SqlDbType.NVarChar, 100).Value = verifiedUser.Username.Trim();
                command.Parameters.Add("@ExpectedVersion", SqlDbType.Binary, 8).Value = verifiedUser.AuthenticationRowVersion;
                command.Parameters.Add("@PasswordHashNew", SqlDbType.NVarChar, 512).Value = newPasswordHash;
                command.Parameters.Add("@PasswordSalt", SqlDbType.NVarChar, 256).Value = newPasswordSalt;

                byte[]? committedVersion = null;
                DateTime? passwordChangedAt = null;
                using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        committedVersion = reader.IsDBNull(0) ? null : (byte[])reader[0];
                        passwordChangedAt = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
                    }
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        throw new InvalidOperationException("Duplicate user identity was detected during password change.");
                }

                if (committedVersion == null || committedVersion.Length != 8)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return false;
                }

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection,
                    transaction,
                    "Users",
                    verifiedUser.UserId,
                    "Password Changed",
                    "Credential retained securely; value not exposed",
                    "Personal credential replaced securely; value not exposed",
                    "Authenticated self-service password change",
                    verifiedUser.Username.Trim(),
                    moduleName: "Authentication");

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                verifiedUser.AuthenticationRowVersion = committedVersion;
                verifiedUser.MustChangePassword = false;
                verifiedUser.PasswordChangedAt = passwordChangedAt;
                return true;
            }
            catch
            {
                if (transaction.Connection != null)
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        public async Task<string> GetRoleAsync(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return "Unknown";

            const string query = @"
                SELECT Role
                FROM Users
                WHERE Username = @UserIdentifier
                  AND ISNULL(IsActive, 1) = 1
                  AND ISNULL(MustChangePassword, 0) = 0
                  AND NOT (ISNULL(IsLocked, 0) = 1 AND (LockedUntil IS NULL OR LockedUntil > SYSDATETIME()))";

            DataTable rows = await _connection.ExecuteQueryAsync(
                query,
                new[] { new SqlParameter("@UserIdentifier", username.Trim()) });

            if (rows.Rows.Count != 1)
                return "Unknown";

            string role = rows.Rows[0]["Role"]?.ToString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(role) ? "Unknown" : role.Trim();
        }

        public async Task<bool> GetPermissionAsync(
            string username,
            string permissionColumn,
            bool fallback = false)
        {
            if (string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(permissionColumn) ||
                !AllowedPermissionColumns.Contains(permissionColumn))
            {
                return fallback;
            }

            string query = $@"
                SELECT ISNULL([{permissionColumn}], 0) AS PermissionGranted
                FROM Users
                WHERE Username = @UserIdentifier
                  AND ISNULL(IsActive, 1) = 1
                  AND ISNULL(MustChangePassword, 0) = 0
                  AND NOT (ISNULL(IsLocked, 0) = 1 AND (LockedUntil IS NULL OR LockedUntil > SYSDATETIME()))";

            DataTable rows = await _connection.ExecuteQueryAsync(
                query,
                new[] { new SqlParameter("@UserIdentifier", username.Trim()) });

            if (rows.Rows.Count != 1)
                return fallback;

            object result = rows.Rows[0]["PermissionGranted"];
            if (result == null || result == DBNull.Value)
                return fallback;

            try
            {
                return Convert.ToBoolean(result);
            }
            catch (FormatException)
            {
                return fallback;
            }
            catch (InvalidCastException)
            {
                return fallback;
            }
        }

        private SqlParameter[] BuildUserParameters(User user)
        {
            return new[]
            {
                new SqlParameter("@UserId", user.UserId),
                new SqlParameter("@Username", user.Username),
                new SqlParameter("@FullName", ToDb(user.FullName)),
                new SqlParameter("@Role", ToDb(user.Role)),
                new SqlParameter("@Department", ToDb(user.Department)),
                new SqlParameter("@Section", ToDb(user.Section)),
                new SqlParameter("@IsActive", user.IsActive),
                new SqlParameter("@CanAccessWater", user.CanAccessWater),
                new SqlParameter("@CanAccessEM", user.CanAccessEM),
                new SqlParameter("@CanRegisterSamples", user.CanRegisterSamples),
                new SqlParameter("@CanEnterResults", user.CanEnterResults),
                new SqlParameter("@CanReviewResults", user.CanReviewResults),
                new SqlParameter("@CanApproveResults", user.CanApproveResults),
                new SqlParameter("@CanIssueCOA", user.CanIssueCOA),
                new SqlParameter("@CanCancelCOA", user.CanCancelCOA),
                new SqlParameter("@CanAccessReports", user.CanAccessReports),
                new SqlParameter("@CanManageUsers", user.CanManageUsers),
                new SqlParameter("@CanManageSettings", user.CanManageSettings)
            };
        }

        private static object ToDb(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
        }

        private static User MapToUser(DataRow row)
        {
            return new User
            {
                UserId = row.GetSafeInt("UserID"),
                Username = row.GetSafeString("Username"),
                FullName = row.GetSafeString("FullName"),
                Role = row.GetSafeString("Role"),
                Department = row.GetSafeString("Department"),
                Section = row.GetSafeString("Section"),

                PasswordHash = row.GetSafeString("PasswordHash"),
                PasswordHashNew = row.GetSafeString("PasswordHashNew"),
                PasswordSalt = row.GetSafeString("PasswordSalt"),
                AuthenticationRowVersion = row.Table.Columns.Contains("AuthenticationRowVersion") &&
                    row["AuthenticationRowVersion"] is byte[] version ? (byte[])version.Clone() : Array.Empty<byte>(),

                IsActive = !row.Table.Columns.Contains("IsActive") ||
                           row["IsActive"] == DBNull.Value ||
                           Convert.ToBoolean(row["IsActive"]),
                LastLogin = row.GetSafeDateTime("LastLogin"),
                FailedLoginAttempts = row.GetSafeInt("FailedLoginAttempts"),
                IsLocked = row.Table.Columns.Contains("IsLocked") &&
                           row["IsLocked"] != DBNull.Value &&
                           Convert.ToBoolean(row["IsLocked"]),
                LockedUntil = row.GetSafeDateTime("LockedUntil"),
                MustChangePassword = row.Table.Columns.Contains("MustChangePassword") &&
                                     row["MustChangePassword"] != DBNull.Value &&
                                     Convert.ToBoolean(row["MustChangePassword"]),
                PasswordChangedAt = row.GetSafeDateTime("PasswordChangedAt"),

                CanAccessWater = GetPermission(row, "CanAccessWater"),
                CanAccessEM = GetPermission(row, "CanAccessEM"),
                CanRegisterSamples = GetPermission(row, "CanRegisterSamples"),
                CanEnterResults = GetPermission(row, "CanEnterResults"),
                CanReviewResults = GetPermission(row, "CanReviewResults"),
                CanApproveResults = GetPermission(row, "CanApproveResults"),
                CanIssueCOA = GetPermission(row, "CanIssueCOA"),
                CanCancelCOA = GetPermission(row, "CanCancelCOA"),
                CanAccessReports = GetPermission(row, "CanAccessReports"),
                CanManageUsers = GetPermission(row, "CanManageUsers"),
                CanManageSettings = GetPermission(row, "CanManageSettings")
            };
        }

        private static bool GetPermission(DataRow row, string column)
        {
            return row.Table.Columns.Contains(column) &&
                   row[column] != DBNull.Value &&
                   Convert.ToBoolean(row[column]);
        }
    }
}