using PharmaLIMS.Models;
using PharmaLIMS.TrustedSigning;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PharmaLIMS.Services;

public static class TrustedApplicationSession
{
    private static string _ticket = "";
    public static string LastCredentialRejection { get; private set; } = "";
    public static void Clear() => _ticket = "";
    public static async Task<User?> AuthenticateAsync(string action, string username, string password, string newPassword, CancellationToken cancellationToken)
    {
        LastCredentialRejection = "";
        if(action=="Reauthenticate") _ticket="";
        try
        {
            var reply = await TrustedEquipmentSigningClient.CreateFromConfiguration().ApplicationAsync(new(Guid.NewGuid(), action, username, password, newPassword), cancellationToken);
            if (!string.IsNullOrEmpty(reply.Ticket)) _ticket = reply.Ticket;
            var user = reply.UserJson is null ? null : JsonSerializer.Deserialize<User>(reply.UserJson);
            user?.ClearSensitivePasswordData();
            return user;
        }
        catch (TrustedSigningRejectedException ex) when(ex.ErrorCode is "CredentialsRejected" or "Locked" or "Inactive") { LastCredentialRejection=ex.ErrorCode; return null; }
    }

    public static async Task<int> AdminAsync(string action, User? user, int id, byte[]? version, string password, string actor, string reason, string meaning, CancellationToken cancellationToken)
    {
        string ticket = _ticket; _ticket = "";
        if (string.IsNullOrEmpty(ticket)) throw new UnauthorizedAccessException("Fresh trusted reauthentication is required for account administration.");
        var request = new TrustedApplicationRequest(Guid.NewGuid(), action, actor, NewPassword:password, Ticket:ticket,
            UserJson:user is null ? "" : JsonSerializer.Serialize(user), UserId:id, ExpectedVersion:version, Reason:reason, Meaning:meaning);
        var reply=await TrustedEquipmentSigningClient.CreateFromConfiguration().ApplicationAsync(request,cancellationToken);
        return int.TryParse(reply.UserJson,out int result)?result:0;
    }
}
