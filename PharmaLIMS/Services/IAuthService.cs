using PharmaLIMS.Models;
using System.Threading;

namespace PharmaLIMS.Services
{
    public interface IAuthService
    {
        Task<User?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default);
        Task<bool> ValidateCurrentUserPasswordAsync(string password);
        Task<bool> ChangeCurrentUserPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);
        User? GetCurrentUser();
        void ClearCurrentUser();
        bool IsUserLoggedIn();
    }
}