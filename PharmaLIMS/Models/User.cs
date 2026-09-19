namespace PharmaLIMS.Models
{
    public class User
    {
        public int UserId { get; set; }
        public string Username { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Role { get; set; } = "";
        public string Department { get; set; } = "";
        public string Section { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public DateTime? LastLogin { get; set; }
        public int FailedLoginAttempts { get; set; }
        public bool IsLocked { get; set; }
        public DateTime? LockedUntil { get; set; }
        public bool MustChangePassword { get; set; }
        public DateTime? PasswordChangedAt { get; set; }

        // Opaque database version used to finalize the exact account that was verified.
        // It is not a password, access token, or an authorization decision.
        internal byte[] AuthenticationRowVersion { get; set; } = Array.Empty<byte>();

        // Password fields are loaded only inside authentication flow.
        // Do not display or expose these values in any UI/report.
        public string PasswordHash { get; set; } = "";
        public string PasswordHashNew { get; set; } = "";
        public string PasswordSalt { get; set; } = "";

        public bool CanAccessWater { get; set; }
        public bool CanAccessEM { get; set; }
        public bool CanRegisterSamples { get; set; }
        public bool CanEnterResults { get; set; }
        public bool CanReviewResults { get; set; }
        public bool CanApproveResults { get; set; }
        public bool CanIssueCOA { get; set; }
        public bool CanCancelCOA { get; set; }
        public bool CanAccessReports { get; set; }
        public bool CanManageUsers { get; set; }
        public bool CanManageSettings { get; set; }

        public bool HasPermission(string permission)
        {
            return permission switch
            {
                "CanAccessWater" => CanAccessWater,
                "CanAccessEM" => CanAccessEM,
                "CanRegisterSamples" => CanRegisterSamples,
                "CanEnterResults" => CanEnterResults,
                "CanReviewResults" => CanReviewResults,
                "CanApproveResults" => CanApproveResults,
                "CanIssueCOA" => CanIssueCOA,
                "CanCancelCOA" => CanCancelCOA,
                "CanAccessReports" => CanAccessReports,
                "CanManageUsers" => CanManageUsers,
                "CanManageSettings" => CanManageSettings,
                _ => false
            };
        }

        public void ClearSensitivePasswordData()
        {
            PasswordHash = "";
            PasswordHashNew = "";
            PasswordSalt = "";
        }
    }
}