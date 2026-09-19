using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Models;
using System.Data;
using System.Globalization;
using System.Text;
using System.Threading;

namespace PharmaLIMS.Services
{
    public sealed class UserAdministrationService
    {
        private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
        {
            "Admin", "Administrator", "Technician", "Supervisor", "QA", "Quality Assurance"
        };
        private readonly DatabaseConnection _database;

        public UserAdministrationService(DatabaseConnection database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public async Task<IReadOnlyList<User>> GetUsersAsync(
            string performedBy,
            CancellationToken cancellationToken = default)
        {
            string actor = RequireActor(performedBy);
            await using SqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await AcquireSchemaStabilityLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            try
            {
                DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection, transaction, actor, "CanManageUsers", "view user accounts and permissions");

                using SqlCommand command = new SqlCommand(@"
SELECT UserID, Username, FullName, Role, Department, Section, IsActive, LastLogin,
       FailedLoginAttempts,
       CONVERT(bit, CASE WHEN ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()) THEN 1 ELSE 0 END) AS IsLockedNow,
       LockedUntil, MustChangePassword, PasswordChangedAt, AuthenticationRowVersion,
       CanAccessWater, CanAccessEM, CanRegisterSamples, CanEnterResults,
       CanReviewResults, CanApproveResults, CanIssueCOA, CanCancelCOA,
       CanAccessReports, CanManageUsers, CanManageSettings
FROM dbo.Users WITH (HOLDLOCK)
ORDER BY Username;", connection, transaction);

                var users = new List<User>();
                using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    users.Add(new User
                    {
                        UserId = reader.GetInt32(0),
                        Username = reader.GetString(1),
                        FullName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        Role = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                        Department = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                        Section = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                        IsActive = !reader.IsDBNull(6) && reader.GetBoolean(6),
                        LastLogin = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                        FailedLoginAttempts = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                        IsLocked = !reader.IsDBNull(9) && reader.GetBoolean(9),
                        LockedUntil = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
                        MustChangePassword = !reader.IsDBNull(11) && reader.GetBoolean(11),
                        PasswordChangedAt = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                        AuthenticationRowVersion = reader.IsDBNull(13) ? Array.Empty<byte>() : (byte[])reader[13],
                        CanAccessWater = !reader.IsDBNull(14) && reader.GetBoolean(14),
                        CanAccessEM = !reader.IsDBNull(15) && reader.GetBoolean(15),
                        CanRegisterSamples = !reader.IsDBNull(16) && reader.GetBoolean(16),
                        CanEnterResults = !reader.IsDBNull(17) && reader.GetBoolean(17),
                        CanReviewResults = !reader.IsDBNull(18) && reader.GetBoolean(18),
                        CanApproveResults = !reader.IsDBNull(19) && reader.GetBoolean(19),
                        CanIssueCOA = !reader.IsDBNull(20) && reader.GetBoolean(20),
                        CanCancelCOA = !reader.IsDBNull(21) && reader.GetBoolean(21),
                        CanAccessReports = !reader.IsDBNull(22) && reader.GetBoolean(22),
                        CanManageUsers = !reader.IsDBNull(23) && reader.GetBoolean(23),
                        CanManageSettings = !reader.IsDBNull(24) && reader.GetBoolean(24)
                    });
                }
                await reader.DisposeAsync().ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return users;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        public async Task<int> CreateUserAsync(
            User user,
            string initialPassword,
            string performedBy,
            string reason,
            string signatureMeaning,
            string signedBy,
            CancellationToken cancellationToken = default)
        {
            ValidateUserForSave(user, isNew: true);
            PasswordSecurity.ValidateNewPassword(initialPassword, user.Username);
            string actor = RequireActor(performedBy);
            string auditReason = RequireReason(reason);
            string meaning = RequireSignatureMeaning(signatureMeaning);
            string signer = RequireSignedActor(signedBy, actor);

            string salt = PasswordSecurity.GenerateSaltBase64();
            string hash = PasswordSecurity.HashToBase64(initialPassword, salt);

            await using SqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await AcquireSchemaStabilityLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            try
            {
                DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection, transaction, actor, "CanManageUsers", "create user accounts");

                using (SqlCommand duplicate = new SqlCommand(@"
SELECT COUNT(1)
FROM dbo.Users WITH (UPDLOCK, HOLDLOCK)
WHERE Username = @Username;", connection, transaction))
                {
                    duplicate.Parameters.Add("@Username", SqlDbType.NVarChar, 100).Value = user.Username.Trim();
                    int existing = Convert.ToInt32(await duplicate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                    if (existing != 0)
                        throw new InvalidOperationException("That username already exists. Use a unique username.");
                }

                int newUserId;
                using (SqlCommand insert = new SqlCommand(@"
INSERT dbo.Users
(
    Username, PasswordHash, PasswordHashNew, PasswordSalt,
    FullName, Role, Department, Section, IsActive, MustChangePassword, PasswordChangedAt,
    FailedLoginAttempts, IsLocked, LockedUntil,
    CanAccessWater, CanAccessEM, CanRegisterSamples, CanEnterResults,
    CanReviewResults, CanApproveResults, CanIssueCOA, CanCancelCOA,
    CanAccessReports, CanManageUsers, CanManageSettings, CreatedAt, UpdatedAt
)
VALUES
(
    @Username, N'[MIGRATED]', @PasswordHashNew, @PasswordSalt,
    @FullName, @Role, @Department, @Section, @IsActive, 1, NULL,
    0, 0, NULL,
    @CanAccessWater, @CanAccessEM, @CanRegisterSamples, @CanEnterResults,
    @CanReviewResults, @CanApproveResults, @CanIssueCOA, @CanCancelCOA,
    @CanAccessReports, @CanManageUsers, @CanManageSettings, SYSUTCDATETIME(), SYSUTCDATETIME()
);
SELECT CONVERT(int, SCOPE_IDENTITY());", connection, transaction))
                {
                    AddUserParameters(insert, user);
                    insert.Parameters.Add("@PasswordHashNew", SqlDbType.NVarChar, 512).Value = hash;
                    insert.Parameters.Add("@PasswordSalt", SqlDbType.NVarChar, 256).Value = salt;
                    newUserId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                }

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection,
                    transaction,
                    "Users",
                    newUserId,
                    "User Account Created",
                    string.Empty,
                    DescribeUser(user),
                    auditReason,
                    actor,
                    moduleName: "User Management");

                await AddUserAdministrationSignatureAsync(
                    connection, transaction, newUserId, user.Username, "User Account Creation",
                    auditReason, meaning, signer, cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return newUserId;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        public async Task UpdateUserAsync(
            User user,
            string performedBy,
            string reason,
            string signatureMeaning,
            string signedBy,
            CancellationToken cancellationToken = default)
        {
            ValidateUserForSave(user, isNew: false);
            string actor = RequireActor(performedBy);
            string auditReason = RequireReason(reason);
            string meaning = RequireSignatureMeaning(signatureMeaning);
            string signer = RequireSignedActor(signedBy, actor);

            await using SqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await AcquireSchemaStabilityLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            try
            {
                DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection, transaction, actor, "CanManageUsers", "update user accounts and permissions");

                UserSnapshot current = await LoadUserSnapshotForUpdateAsync(connection, transaction, user.UserId, cancellationToken).ConfigureAwait(false);
                EnsureExpectedVersion(user.AuthenticationRowVersion, current.AuthenticationRowVersion);

                if (!current.Username.Equals(user.Username.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Username is immutable after account creation. Create a new account instead of renaming an existing identity.");

                bool editingSelf = current.Username.Equals(actor, StringComparison.OrdinalIgnoreCase);
                if (editingSelf && !user.IsActive)
                    throw new InvalidOperationException("You cannot deactivate the account currently being used to administer users.");
                if (editingSelf && AdministrativeAccountChanged(current, user))
                    throw new InvalidOperationException(
                        "You cannot administratively modify your own PharmaLIMS account. Use Change Password for your personal credential; another authorized User Manager must perform identity, status, Role, or permission changes.");

                if (!AdministrativeAccountChanged(current, user))
                    throw new InvalidOperationException(
                        "No user-account changes were detected. Nothing was saved, audited, or electronically signed.");

                if (current.IsActive && current.CanManageUsers && (!user.IsActive || !user.CanManageUsers))
                    await EnsureAnotherActiveUserManagerExistsAsync(connection, transaction, user.UserId, cancellationToken).ConfigureAwait(false);

                using (SqlCommand update = new SqlCommand(@"
UPDATE dbo.Users
SET FullName = @FullName,
    Role = @Role,
    Department = @Department,
    Section = @Section,
    IsActive = @IsActive,
    CanAccessWater = @CanAccessWater,
    CanAccessEM = @CanAccessEM,
    CanRegisterSamples = @CanRegisterSamples,
    CanEnterResults = @CanEnterResults,
    CanReviewResults = @CanReviewResults,
    CanApproveResults = @CanApproveResults,
    CanIssueCOA = @CanIssueCOA,
    CanCancelCOA = @CanCancelCOA,
    CanAccessReports = @CanAccessReports,
    CanManageUsers = @CanManageUsers,
    CanManageSettings = @CanManageSettings,
    UpdatedAt = SYSUTCDATETIME()
WHERE UserID = @UserID
  AND AuthenticationRowVersion = @ExpectedVersion;", connection, transaction))
                {
                    AddUserParameters(update, user);
                    update.Parameters.Add("@UserID", SqlDbType.Int).Value = user.UserId;
                    update.Parameters.Add("@ExpectedVersion", SqlDbType.Binary, 8).Value = user.AuthenticationRowVersion;
                    int affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    if (affected != 1)
                        throw new InvalidOperationException("The selected user account changed after it was loaded. Refresh User Management and review the latest values before saving.");
                }

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection,
                    transaction,
                    "Users",
                    user.UserId,
                    "User Account Updated",
                    current.Describe(),
                    DescribeUser(user),
                    auditReason,
                    actor,
                    moduleName: "User Management");

                await AddUserAdministrationSignatureAsync(
                    connection, transaction, user.UserId, user.Username, "User Account and Permission Update",
                    auditReason, meaning, signer, cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        public async Task ResetPasswordAsync(
            int userId,
            string newPassword,
            string performedBy,
            string reason,
            string signatureMeaning,
            string signedBy,
            byte[] expectedVersion,
            CancellationToken cancellationToken = default)
        {
            if (userId <= 0)
                throw new ArgumentOutOfRangeException(nameof(userId));
            PasswordSecurity.ValidateNewPassword(newPassword);
            string actor = RequireActor(performedBy);
            string auditReason = RequireReason(reason);
            string meaning = RequireSignatureMeaning(signatureMeaning);
            string signer = RequireSignedActor(signedBy, actor);

            string salt = PasswordSecurity.GenerateSaltBase64();
            string hash = PasswordSecurity.HashToBase64(newPassword, salt);

            await using SqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await AcquireSchemaStabilityLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            try
            {
                DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection, transaction, actor, "CanManageUsers", "reset user passwords");

                UserSnapshot current = await LoadUserSnapshotForUpdateAsync(connection, transaction, userId, cancellationToken).ConfigureAwait(false);
                EnsureExpectedVersion(expectedVersion, current.AuthenticationRowVersion);
                if (current.Username.Equals(actor, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Use Change Password for your own account. Administrative reset is for other users only.");
                PasswordSecurity.ValidateNewPassword(newPassword, current.Username);

                using SqlCommand update = new SqlCommand(@"
UPDATE dbo.Users
SET PasswordHash = N'[MIGRATED]',
    PasswordHashNew = @PasswordHashNew,
    PasswordSalt = @PasswordSalt,
    MustChangePassword = 1,
    PasswordChangedAt = NULL,
    FailedLoginAttempts = 0,
    IsLocked = 0,
    LockedUntil = NULL,
    UpdatedAt = SYSUTCDATETIME()
WHERE UserID = @UserID
  AND AuthenticationRowVersion = @ExpectedVersion;", connection, transaction);
                update.Parameters.Add("@PasswordHashNew", SqlDbType.NVarChar, 512).Value = hash;
                update.Parameters.Add("@PasswordSalt", SqlDbType.NVarChar, 256).Value = salt;
                update.Parameters.Add("@UserID", SqlDbType.Int).Value = userId;
                update.Parameters.Add("@ExpectedVersion", SqlDbType.Binary, 8).Value = expectedVersion;
                int affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (affected != 1)
                    throw new InvalidOperationException("The selected account changed after it was loaded. Refresh before resetting its password.");

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection,
                    transaction,
                    "Users",
                    userId,
                    "User Password Reset",
                    "Credential retained securely; value not exposed",
                    "Credential replaced securely; value not exposed",
                    auditReason,
                    actor,
                    moduleName: "User Management");

                await AddUserAdministrationSignatureAsync(
                    connection, transaction, userId, current.Username, "User Password Reset",
                    auditReason, meaning, signer, cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                ApplicationLogger.Information($"User password reset completed for account '{current.Username}' by '{actor}'.");
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        public async Task UnlockUserAsync(
            int userId,
            string performedBy,
            string reason,
            string signatureMeaning,
            string signedBy,
            byte[] expectedVersion,
            CancellationToken cancellationToken = default)
        {
            if (userId <= 0)
                throw new ArgumentOutOfRangeException(nameof(userId));
            string actor = RequireActor(performedBy);
            string auditReason = RequireReason(reason);
            string meaning = RequireSignatureMeaning(signatureMeaning);
            string signer = RequireSignedActor(signedBy, actor);

            await using SqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await AcquireSchemaStabilityLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            try
            {
                DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection, transaction, actor, "CanManageUsers", "unlock user accounts");

                UserSnapshot current = await LoadUserSnapshotForUpdateAsync(connection, transaction, userId, cancellationToken).ConfigureAwait(false);
                EnsureExpectedVersion(expectedVersion, current.AuthenticationRowVersion);
                if (!current.IsLocked && current.FailedLoginAttempts <= 0)
                    throw new InvalidOperationException("The selected account is not locked and has no failed-login counter to clear.");

                using SqlCommand update = new SqlCommand(@"
UPDATE dbo.Users
SET FailedLoginAttempts = 0,
    IsLocked = 0,
    LockedUntil = NULL,
    UpdatedAt = SYSUTCDATETIME()
WHERE UserID = @UserID
  AND AuthenticationRowVersion = @ExpectedVersion;", connection, transaction);
                update.Parameters.Add("@UserID", SqlDbType.Int).Value = userId;
                update.Parameters.Add("@ExpectedVersion", SqlDbType.Binary, 8).Value = expectedVersion;
                int affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (affected != 1)
                    throw new InvalidOperationException("The selected account changed after it was loaded. Refresh before unlocking it.");

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection,
                    transaction,
                    "Users",
                    userId,
                    "User Account Unlocked",
                    current.IsLocked ? "Locked" : "Not locked",
                    "Unlocked",
                    auditReason,
                    actor,
                    moduleName: "User Management");

                await AddUserAdministrationSignatureAsync(
                    connection, transaction, userId, current.Username, "User Account Unlock",
                    auditReason, meaning, signer, cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        private static async Task AcquireSchemaStabilityLockAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            using SqlCommand command = new SqlCommand(@"
DECLARE @LockResult INT;
EXEC @LockResult=sys.sp_getapplock
    @Resource=N'PharmaLIMS.SchemaMigration',
    @LockMode=N'Shared',
    @LockOwner=N'Transaction',
    @LockTimeout=15000;
IF @LockResult < 0
    THROW 53011, 'User administration is blocked while controlled Database Maintenance is active.', 1;", connection, transaction)
            {
                CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 20)
            };
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<UserSnapshot> LoadUserSnapshotForUpdateAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            int userId,
            CancellationToken cancellationToken)
        {
            using SqlCommand command = new SqlCommand(@"
SELECT UserID, Username, FullName, Role, Department, Section, IsActive,
       CanAccessWater, CanAccessEM, CanRegisterSamples, CanEnterResults,
       CanReviewResults, CanApproveResults, CanIssueCOA, CanCancelCOA,
       CanAccessReports, CanManageUsers, CanManageSettings, MustChangePassword, AuthenticationRowVersion, FailedLoginAttempts,
       CASE WHEN ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()) THEN 1 ELSE 0 END AS IsLockedNow
FROM dbo.Users WITH (UPDLOCK, HOLDLOCK)
WHERE UserID = @UserID;", connection, transaction);
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userId;

            using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The selected user account no longer exists.");

            UserSnapshot snapshot = new UserSnapshot(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                !reader.IsDBNull(6) && reader.GetBoolean(6),
                !reader.IsDBNull(7) && reader.GetBoolean(7),
                !reader.IsDBNull(8) && reader.GetBoolean(8),
                !reader.IsDBNull(9) && reader.GetBoolean(9),
                !reader.IsDBNull(10) && reader.GetBoolean(10),
                !reader.IsDBNull(11) && reader.GetBoolean(11),
                !reader.IsDBNull(12) && reader.GetBoolean(12),
                !reader.IsDBNull(13) && reader.GetBoolean(13),
                !reader.IsDBNull(14) && reader.GetBoolean(14),
                !reader.IsDBNull(15) && reader.GetBoolean(15),
                !reader.IsDBNull(16) && reader.GetBoolean(16),
                !reader.IsDBNull(17) && reader.GetBoolean(17),
                !reader.IsDBNull(18) && reader.GetBoolean(18),
                reader.IsDBNull(19) ? Array.Empty<byte>() : (byte[])reader[19],
                reader.IsDBNull(20) ? 0 : reader.GetInt32(20),
                !reader.IsDBNull(21) && reader.GetBoolean(21));

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Duplicate user identity was detected. User administration is blocked.");

            return snapshot;
        }

        private static async Task EnsureAnotherActiveUserManagerExistsAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            int excludedUserId,
            CancellationToken cancellationToken)
        {
            using SqlCommand command = new SqlCommand(@"
SELECT COUNT(1)
FROM dbo.Users WITH (UPDLOCK, HOLDLOCK)
WHERE UserID <> @UserID
  AND ISNULL(IsActive,1)=1
  AND ISNULL(CanManageUsers,0)=1
  AND ISNULL(MustChangePassword,0)=0
  AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()));", connection, transaction);
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = excludedUserId;
            int count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (count < 1)
                throw new InvalidOperationException("At least one other active account with User Management permission must remain enabled.");
        }

        private static async Task AddUserAdministrationSignatureAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            int targetUserId,
            string targetUsername,
            string actionType,
            string actionReason,
            string meaningOfSignature,
            string signedBy,
            CancellationToken cancellationToken)
        {
            using SqlCommand command = new SqlCommand(@"
INSERT dbo.UserAdministrationSignatures
(
    TargetUserID, TargetUsername, ActionType, ActionReason, MeaningOfSignature,
    SignedBy, UserRole, SignedAt, SourceWorkstation, SourceApplication
)
SELECT
    @TargetUserID, @TargetUsername, @ActionType, @ActionReason, @MeaningOfSignature,
    @SignedBy, signer.Role, SYSUTCDATETIME(), HOST_NAME(), N'PharmaLIMS'
FROM dbo.Users signer WITH (HOLDLOCK)
WHERE signer.Username = @SignedBy;
IF @@ROWCOUNT <> 1
    THROW 55220, 'Electronic-signature signer identity could not be bound to exactly one PharmaLIMS user.', 1;", connection, transaction);
            command.Parameters.Add("@TargetUserID", SqlDbType.Int).Value = targetUserId;
            command.Parameters.Add("@TargetUsername", SqlDbType.NVarChar, 100).Value = targetUsername.Trim();
            command.Parameters.Add("@ActionType", SqlDbType.NVarChar, 100).Value = actionType.Trim();
            command.Parameters.Add("@ActionReason", SqlDbType.NVarChar, 1000).Value = actionReason.Trim();
            command.Parameters.Add("@MeaningOfSignature", SqlDbType.NVarChar, 255).Value = meaningOfSignature.Trim();
            command.Parameters.Add("@SignedBy", SqlDbType.NVarChar, 100).Value = signedBy.Trim();
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static void AddUserParameters(SqlCommand command, User user)
        {
            command.Parameters.Add("@Username", SqlDbType.NVarChar, 100).Value = user.Username.Trim();
            command.Parameters.Add("@FullName", SqlDbType.NVarChar, 200).Value = user.FullName.Trim();
            command.Parameters.Add("@Role", SqlDbType.NVarChar, 100).Value = user.Role.Trim();
            command.Parameters.Add("@Department", SqlDbType.NVarChar, 150).Value = DbText(user.Department);
            command.Parameters.Add("@Section", SqlDbType.NVarChar, 150).Value = DbText(user.Section);
            command.Parameters.Add("@IsActive", SqlDbType.Bit).Value = user.IsActive;
            command.Parameters.Add("@CanAccessWater", SqlDbType.Bit).Value = user.CanAccessWater;
            command.Parameters.Add("@CanAccessEM", SqlDbType.Bit).Value = user.CanAccessEM;
            command.Parameters.Add("@CanRegisterSamples", SqlDbType.Bit).Value = user.CanRegisterSamples;
            command.Parameters.Add("@CanEnterResults", SqlDbType.Bit).Value = user.CanEnterResults;
            command.Parameters.Add("@CanReviewResults", SqlDbType.Bit).Value = user.CanReviewResults;
            command.Parameters.Add("@CanApproveResults", SqlDbType.Bit).Value = user.CanApproveResults;
            command.Parameters.Add("@CanIssueCOA", SqlDbType.Bit).Value = user.CanIssueCOA;
            command.Parameters.Add("@CanCancelCOA", SqlDbType.Bit).Value = user.CanCancelCOA;
            command.Parameters.Add("@CanAccessReports", SqlDbType.Bit).Value = user.CanAccessReports;
            command.Parameters.Add("@CanManageUsers", SqlDbType.Bit).Value = user.CanManageUsers;
            command.Parameters.Add("@CanManageSettings", SqlDbType.Bit).Value = user.CanManageSettings;
        }

        private static object DbText(string? value) =>
            string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

        private static string RequireActor(string performedBy)
        {
            if (string.IsNullOrWhiteSpace(performedBy))
                throw new UnauthorizedAccessException("An authenticated user is required for User Management.");
            return performedBy.Trim();
        }

        private static string RequireReason(string reason)
        {
            string normalized = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
            if (normalized.Length < 10)
                throw new InvalidOperationException("A specific electronic-signature justification of at least 10 characters is required for user administration.");

            string[] genericReasons =
            {
                "User account administration",
                "Routine data entry",
                "Result entry",
                "Correction of data entry error",
                "Review of data",
                "Approval of results",
                "Certificate issuance",
                "Certificate cancellation",
                "OOS investigation",
                "Quality event closure",
                "EM result entry",
                "Database maintenance"
            };

            if (genericReasons.Any(item => normalized.Equals(item, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Enter a specific justification for this user-administration change; a generic preset reason is not sufficient.");

            return normalized;
        }

        private static string RequireSignatureMeaning(string meaning)
        {
            if (string.IsNullOrWhiteSpace(meaning))
                throw new InvalidOperationException("Electronic-signature meaning is required for user administration.");
            return meaning.Trim();
        }

        private static string RequireSignedActor(string signedBy, string actor)
        {
            string signer = string.IsNullOrWhiteSpace(signedBy) ? string.Empty : signedBy.Trim();
            if (!signer.Equals(actor, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Electronic signature must be completed by the currently authenticated User Manager.");
            return signer;
        }

        private static void ValidateUserForSave(User user, bool isNew)
        {
            ArgumentNullException.ThrowIfNull(user);
            if (!isNew && user.UserId <= 0)
                throw new InvalidOperationException("Select an existing user account before saving changes.");
            if (string.IsNullOrWhiteSpace(user.Username))
                throw new InvalidOperationException("Username is required.");
            if (user.Username.Trim().Length > 100)
                throw new InvalidOperationException("Username cannot exceed 100 characters.");
            if (string.IsNullOrWhiteSpace(user.FullName))
                throw new InvalidOperationException("Full Name is required.");
            if (string.IsNullOrWhiteSpace(user.Role))
                throw new InvalidOperationException("Role is required.");
            if (!AllowedRoles.Contains(user.Role.Trim()))
                throw new InvalidOperationException("Role must be selected from the controlled PharmaLIMS role list.");
        }

        private static void EnsureExpectedVersion(byte[] expected, byte[] actual)
        {
            if (expected == null || expected.Length != 8 || actual == null || actual.Length != 8 ||
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new InvalidOperationException(
                    "The selected user account changed after it was loaded. Refresh User Management before performing this action.");
            }
        }

        private static bool AdministrativeAccountChanged(UserSnapshot current, User proposed)
        {
            return !current.FullName.Equals(proposed.FullName.Trim(), StringComparison.Ordinal) ||
                   !current.Department.Equals(proposed.Department?.Trim() ?? string.Empty, StringComparison.Ordinal) ||
                   !current.Section.Equals(proposed.Section?.Trim() ?? string.Empty, StringComparison.Ordinal) ||
                   current.IsActive != proposed.IsActive ||
                   !current.Role.Equals(proposed.Role.Trim(), StringComparison.OrdinalIgnoreCase) ||
                   current.CanAccessWater != proposed.CanAccessWater ||
                   current.CanAccessEM != proposed.CanAccessEM ||
                   current.CanRegisterSamples != proposed.CanRegisterSamples ||
                   current.CanEnterResults != proposed.CanEnterResults ||
                   current.CanReviewResults != proposed.CanReviewResults ||
                   current.CanApproveResults != proposed.CanApproveResults ||
                   current.CanIssueCOA != proposed.CanIssueCOA ||
                   current.CanCancelCOA != proposed.CanCancelCOA ||
                   current.CanAccessReports != proposed.CanAccessReports ||
                   current.CanManageUsers != proposed.CanManageUsers ||
                   current.CanManageSettings != proposed.CanManageSettings;
        }

        private static string DescribeUser(User user)
        {
            var permissions = new List<string>();
            if (user.CanAccessWater) permissions.Add("Water");
            if (user.CanAccessEM) permissions.Add("EM");
            if (user.CanRegisterSamples) permissions.Add("Register");
            if (user.CanEnterResults) permissions.Add("EnterResults");
            if (user.CanReviewResults) permissions.Add("Review");
            if (user.CanApproveResults) permissions.Add("Approve");
            if (user.CanIssueCOA) permissions.Add("IssueCOA");
            if (user.CanCancelCOA) permissions.Add("CancelCOA");
            if (user.CanAccessReports) permissions.Add("Reports");
            if (user.CanManageUsers) permissions.Add("ManageUsers");
            if (user.CanManageSettings) permissions.Add("ManageSettings");

            return $"Username={user.Username.Trim()}; FullName={user.FullName.Trim()}; Role={user.Role.Trim()}; " +
                   $"Department={user.Department?.Trim()}; Section={user.Section?.Trim()}; Active={user.IsActive}; " +
                   $"Permissions=[{string.Join(",", permissions)}]";
        }

        private sealed record UserSnapshot(
            int UserId,
            string Username,
            string FullName,
            string Role,
            string Department,
            string Section,
            bool IsActive,
            bool CanAccessWater,
            bool CanAccessEM,
            bool CanRegisterSamples,
            bool CanEnterResults,
            bool CanReviewResults,
            bool CanApproveResults,
            bool CanIssueCOA,
            bool CanCancelCOA,
            bool CanAccessReports,
            bool CanManageUsers,
            bool CanManageSettings,
            bool MustChangePassword,
            byte[] AuthenticationRowVersion,
            int FailedLoginAttempts,
            bool IsLocked)
        {
            public string Describe()
            {
                var user = new User
                {
                    UserId = UserId,
                    Username = Username,
                    FullName = FullName,
                    Role = Role,
                    Department = Department,
                    Section = Section,
                    IsActive = IsActive,
                    CanAccessWater = CanAccessWater,
                    CanAccessEM = CanAccessEM,
                    CanRegisterSamples = CanRegisterSamples,
                    CanEnterResults = CanEnterResults,
                    CanReviewResults = CanReviewResults,
                    CanApproveResults = CanApproveResults,
                    CanIssueCOA = CanIssueCOA,
                    CanCancelCOA = CanCancelCOA,
                    CanAccessReports = CanAccessReports,
                    CanManageUsers = CanManageUsers,
                    CanManageSettings = CanManageSettings
                };
                return DescribeUser(user);
            }
        }
    }
}
