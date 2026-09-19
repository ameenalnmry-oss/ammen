using System.Threading;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Models;
using PharmaLIMS.Repositories;

namespace PharmaLIMS.Services
{
    public class AuthService : IAuthService
    {
        private readonly UserRepository _userRepository;
        private User? _currentUser;

        public AuthService(UserRepository userRepository)
        {
            _userRepository = userRepository;
        }

        public async Task<User?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
                return null;

            username = username.Trim();
            cancellationToken.ThrowIfCancellationRequested();
            _currentUser = null;

            User? user = await _userRepository.GetByUsernameAsync(username, cancellationToken).ConfigureAwait(false);

            if (user == null)
            {
                ApplicationLogger.Warning($"Authentication rejected before credential verification: account '{username}' was not found.");
                return null;
            }

            if (!user.IsActive)
            {
                ApplicationLogger.Warning($"Authentication rejected before credential verification: account '{username}' is inactive.");
                return null;
            }

            if (IsLockedNow(user))
            {
                ApplicationLogger.Warning($"Authentication rejected before credential verification: account '{username}' is currently locked.");
                return null;
            }

            PasswordVerificationResult verification = await Task.Run(
                () => VerifyPassword(password, user),
                cancellationToken).ConfigureAwait(false);

            if (verification == PasswordVerificationResult.Invalid)
            {
                ApplicationLogger.Warning($"Authentication credential verification failed for account '{username}'.");
                await _userRepository.RegisterAuthenticationFailureAsync(user, cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (verification == PasswordVerificationResult.ValidLegacy)
            {
                // A verified legacy credential is never accepted as the session credential.
                // The existing compare-and-swap success commit must first replace it with
                // a current PBKDF2-SHA256 hash + salt and mark PasswordHash as [MIGRATED].
                // If that atomic upgrade loses a race, is blocked, or fails, no session is created.
                ApplicationLogger.Warning(
                    $"One-time secure credential migration started for account '{username}' after successful legacy-password verification.");
            }

            // The rowversion captured with the password also covers lock, role,
            // permission and credential changes during verification. No stale retry.
            if (!await FinalizeVerifiedUserAsync(user, password, verification, true, cancellationToken).ConfigureAwait(false))
                return null;

            user.ClearSensitivePasswordData();
            _currentUser = user;
            return user;
        }

        public async Task<bool> ValidateCurrentUserPasswordAsync(string password)
        {
            if (_currentUser == null || string.IsNullOrEmpty(password))
                return false;

            User? freshUser = await _userRepository.GetByUsernameAsync(_currentUser.Username);

            if (freshUser == null || !freshUser.IsActive || IsLockedNow(freshUser))
            {
                _currentUser = null;
                Login.ClearCurrentUserContext();
                ApplicationLogger.Warning("Active session ended because the authenticated account is missing, inactive, or locked during electronic-signature validation.");
                return false;
            }

            if (freshUser.MustChangePassword)
            {
                _currentUser = null;
                Login.ClearCurrentUserContext();
                ApplicationLogger.Warning(
                    $"Electronic-signature validation blocked because account '{freshUser.Username}' must change its temporary password first.");
                return false;
            }

            PasswordVerificationResult verification = await Task.Run(() => VerifyPassword(password, freshUser)).ConfigureAwait(false);

            if (verification == PasswordVerificationResult.Invalid)
            {
                await _userRepository.RegisterAuthenticationFailureAsync(freshUser).ConfigureAwait(false);

                User? updatedUser = await _userRepository.GetByUsernameAsync(freshUser.Username).ConfigureAwait(false);
                if (updatedUser == null || !updatedUser.IsActive || IsLockedNow(updatedUser))
                {
                    _currentUser = null;
                    Login.ClearCurrentUserContext();
                    ApplicationLogger.Warning($"Active session ended because account '{freshUser.Username}' is missing, inactive, or locked after failed electronic-signature validation.");
                }

                return false;
            }

            if (verification == PasswordVerificationResult.ValidLegacy && AppConfig.IsProduction)
            {
                _currentUser = null;
                Login.ClearCurrentUserContext();
                ApplicationLogger.Warning(
                    $"Electronic-signature validation blocked because account '{freshUser.Username}' still uses a legacy credential in Production.");
                return false;
            }

            bool committed = await FinalizeVerifiedUserAsync(
                freshUser, password, verification, false, CancellationToken.None).ConfigureAwait(false);
            if (!committed)
            {
                _currentUser = null;
                Login.ClearCurrentUserContext();
                return false;
            }

            freshUser.ClearSensitivePasswordData();
            _currentUser = freshUser;
            return true;
        }

        public async Task<bool> ChangeCurrentUserPasswordAsync(
            string currentPassword,
            string newPassword,
            CancellationToken cancellationToken = default)
        {
            if (_currentUser == null || string.IsNullOrEmpty(currentPassword))
                return false;

            PasswordSecurity.ValidateNewPassword(newPassword, _currentUser.Username);
            if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
                throw new InvalidOperationException("The new password must be different from the current password.");

            User? freshUser = await _userRepository.GetByUsernameAsync(_currentUser.Username, cancellationToken).ConfigureAwait(false);
            if (freshUser == null || !freshUser.IsActive || IsLockedNow(freshUser))
            {
                _currentUser = null;
                Login.ClearCurrentUserContext();
                return false;
            }

            PasswordVerificationResult verification = await Task.Run(
                () => VerifyPassword(currentPassword, freshUser), cancellationToken).ConfigureAwait(false);
            if (verification == PasswordVerificationResult.Invalid)
            {
                await _userRepository.RegisterAuthenticationFailureAsync(freshUser, cancellationToken).ConfigureAwait(false);
                User? updatedUser = await _userRepository.GetByUsernameAsync(freshUser.Username, cancellationToken).ConfigureAwait(false);
                if (updatedUser == null || !updatedUser.IsActive || IsLockedNow(updatedUser))
                {
                    _currentUser = null;
                    Login.ClearCurrentUserContext();
                    ApplicationLogger.Warning(
                        $"Active session ended because account '{freshUser.Username}' became unavailable after failed password-change verification.");
                }
                return false;
            }

            string newSalt = PasswordSecurity.GenerateSaltBase64();
            string newHash = await Task.Run(
                () => PasswordSecurity.HashToBase64(newPassword, newSalt), cancellationToken).ConfigureAwait(false);

            bool committed = await _userRepository.ChangeVerifiedPasswordAsync(
                freshUser, newHash, newSalt, cancellationToken).ConfigureAwait(false);
            if (!committed)
                return false;

            freshUser.ClearSensitivePasswordData();
            freshUser.MustChangePassword = false;
            _currentUser = freshUser;
            ApplicationLogger.Information($"Password changed by account '{freshUser.Username}' using the authenticated self-service flow.");
            return true;
        }

        public User? GetCurrentUser()
        {
            return _currentUser;
        }

        public void ClearCurrentUser()
        {
            _currentUser = null;
        }

        public bool IsUserLoggedIn()
        {
            return _currentUser != null;
        }

        private static bool IsLockedNow(User user)
        {
            // UserRepository calculates the effective lock with SQL Server SYSDATETIME().
            // The workstation clock is deliberately not trusted for authentication state.
            return user.IsLocked;
        }

        private async Task<bool> FinalizeVerifiedUserAsync(
            User user, string password, PasswordVerificationResult verification, bool isLogin,
            CancellationToken cancellationToken)
        {
            string? upgradedHash = null;
            string? upgradedSalt = null;
            if (verification is PasswordVerificationResult.ValidLegacy or PasswordVerificationResult.ValidSecureNeedsUpgrade)
            {
                upgradedSalt = PasswordSecurity.GenerateSaltBase64();
                string salt = upgradedSalt;
                upgradedHash = await Task.Run(
                    () => PasswordSecurity.HashToBase64(password, salt), cancellationToken).ConfigureAwait(false);
            }

            bool committed = isLogin
                ? await _userRepository.RecordSuccessfulLoginAsync(
                    user, upgradedHash, upgradedSalt, cancellationToken).ConfigureAwait(false)
                : await _userRepository.ResetAuthenticationFailuresAsync(
                    user, upgradedHash, upgradedSalt, cancellationToken).ConfigureAwait(false);
            if (!committed)
            {
                ApplicationLogger.Warning(
                    "Authentication was rejected because the verified account changed, was locked, or was deactivated before commit.");
                return false;
            }
            if (upgradedHash != null)
                ApplicationLogger.Information("Password credential upgrade committed with guarded authentication.");
            return true;
        }

        private static PasswordVerificationResult VerifyPassword(string password, User user)
        {
            if (string.IsNullOrEmpty(password))
                return PasswordVerificationResult.Invalid;

            bool hasSecurePassword =
                !string.IsNullOrWhiteSpace(user.PasswordHashNew) &&
                !string.IsNullOrWhiteSpace(user.PasswordSalt);

            // A structurally valid secure credential is authoritative. A password mismatch
            // must never downgrade to the legacy field. The legacy repair path below is
            // permitted only when a previous transition left malformed secure encoding
            // AND the retained legacy credential independently verifies the supplied password.
            if (hasSecurePassword)
            {
                bool secureEncodingValid = PasswordSecurity.IsCredentialEncodingStructurallyValid(
                    user.PasswordSalt,
                    user.PasswordHashNew);

                if (secureEncodingValid)
                {
                    bool valid = PasswordSecurity.Verify(
                        password,
                        user.PasswordSalt,
                        user.PasswordHashNew,
                        out bool needsUpgrade);

                    if (!valid)
                        return PasswordVerificationResult.Invalid;

                    return needsUpgrade
                        ? PasswordVerificationResult.ValidSecureNeedsUpgrade
                        : PasswordVerificationResult.ValidSecure;
                }

                if (HasVerifiedLegacyCredential(password, user))
                {
                    ApplicationLogger.Warning(
                        $"Malformed secure credential encoding detected for account '{user.Username}'. " +
                        "A verified retained legacy credential will be replaced atomically before any session is created.");
                    return PasswordVerificationResult.ValidLegacy;
                }

                return PasswordVerificationResult.Invalid;
            }

            return HasVerifiedLegacyCredential(password, user)
                ? PasswordVerificationResult.ValidLegacy
                : PasswordVerificationResult.Invalid;
        }

        private static bool HasVerifiedLegacyCredential(string password, User user)
        {
            return !string.IsNullOrEmpty(user.PasswordHash) &&
                   !string.Equals(user.PasswordHash, "[MIGRATED]", StringComparison.Ordinal) &&
                   string.Equals(user.PasswordHash, password, StringComparison.Ordinal);
        }

        private enum PasswordVerificationResult
        {
            Invalid,
            ValidSecure,
            ValidSecureNeedsUpgrade,
            ValidLegacy
        }
    }
}
