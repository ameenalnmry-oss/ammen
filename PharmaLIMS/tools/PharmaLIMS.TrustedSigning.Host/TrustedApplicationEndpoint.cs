using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Models;
using PharmaLIMS.Repositories;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

internal sealed class TrustedApplicationEndpoint(string connectionString) : ITrustedApplicationEndpoint
{
    private sealed record Ticket(string Sid,string Username,DateTimeOffset Expires,byte[] Version);
    private readonly ConcurrentDictionary<string,Ticket> _tickets = new();
    public async Task<TrustedSigningReply> HandleAsync(string sid,TrustedApplicationRequest request,CancellationToken cancellationToken)
    {
        try
        {
            if(request.RequestId==Guid.Empty || string.IsNullOrWhiteSpace(request.Action) || request.Action.Length>30 || string.IsNullOrWhiteSpace(request.Username) || request.Username.Length>100 ||
                request.Password is null || request.NewPassword is null || request.Ticket is null || request.UserJson is null || request.Reason is null || request.Meaning is null ||
                request.Password.Length>4096 || request.NewPassword.Length>4096 || request.Ticket.Length>128 || request.UserJson.Length>32768 || request.Reason.Length>500 || request.Meaning.Length>250)
                return new(request.RequestId,false,null,"InvalidRequest");
            var database = new DatabaseConnection(connectionString);
            var repository = new UserRepository(database);
            if(request.Action is "Login" or "Reauthenticate" or "ChangePassword")
            {
                var auth = new AuthService(repository, useTrustedTransport:false);
                User? user = await auth.AuthenticateAsync(request.Username,request.Password,cancellationToken);
                if(user is null)
                {
                    var state = await repository.GetByUsernameAsync(request.Username,cancellationToken);
                    return new(request.RequestId,false,null,state?.IsLocked==true?"Locked":state?.IsActive==false?"Inactive":"CredentialsRejected");
                }
                if(request.Action=="ChangePassword")
                {
                    if(!await auth.ChangeCurrentUserPasswordAsync(request.Password,request.NewPassword,cancellationToken))return new(request.RequestId,false,null,"CredentialsRejected");
                    user=auth.GetCurrentUser()!;
                }
                if(request.Action=="Reauthenticate" && user.MustChangePassword)return new(request.RequestId,false,null,"Inactive");
                string? ticket=null;
                if(request.Action=="Reauthenticate")
                {
                    ticket=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    _tickets[ticket]=new(sid,user.Username,DateTimeOffset.UtcNow.AddSeconds(30),user.AuthenticationRowVersion);
                    foreach(var entry in _tickets)if(entry.Value.Expires<DateTimeOffset.UtcNow)_tickets.TryRemove(entry.Key,out _);
                }
                user.ClearSensitivePasswordData();
                return new(request.RequestId,true,user.Username,"",JsonSerializer.Serialize(user),ticket);
            }
            if(request.Action is not ("CreateUser" or "UpdateUser" or "ResetPassword" or "UnlockUser"))return new(request.RequestId,false,null,"InvalidRequest");
            if(!_tickets.TryRemove(request.Ticket,out var proof) || proof.Sid!=sid || !proof.Username.Equals(request.Username,StringComparison.OrdinalIgnoreCase) || proof.Expires<DateTimeOffset.UtcNow)
                return new(request.RequestId,false,null,"Denied");
            var actor=await repository.GetByUsernameAsync(proof.Username,cancellationToken);
            if(actor is null || !actor.IsActive || actor.IsLocked || actor.MustChangePassword || !actor.CanManageUsers || !actor.AuthenticationRowVersion.SequenceEqual(proof.Version))return new(request.RequestId,false,null,"Denied");
            var administration=new UserAdministrationService(database,useTrustedTransport:false);
            int result=0;
            switch(request.Action)
            {
                case "CreateUser": result=await administration.CreateUserAsync(JsonSerializer.Deserialize<User>(request.UserJson)??throw new ArgumentException("Missing user."),request.NewPassword,proof.Username,request.Reason,request.Meaning,proof.Username,cancellationToken);break;
                case "UpdateUser": var updated=JsonSerializer.Deserialize<User>(request.UserJson)??throw new ArgumentException("Missing user.");updated.AuthenticationRowVersion=request.ExpectedVersion??Array.Empty<byte>();await administration.UpdateUserAsync(updated,proof.Username,request.Reason,request.Meaning,proof.Username,cancellationToken);break;
                case "ResetPassword": await administration.ResetPasswordAsync(request.UserId,request.NewPassword,proof.Username,request.Reason,request.Meaning,proof.Username,request.ExpectedVersion!,cancellationToken);break;
                case "UnlockUser": await administration.UnlockUserAsync(request.UserId,proof.Username,request.Reason,request.Meaning,proof.Username,request.ExpectedVersion!,cancellationToken);break;
            }
            return new(request.RequestId,true,proof.Username,"",result.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch(ArgumentException){return new(request.RequestId,false,null,"InvalidRequest");}
        catch(JsonException){return new(request.RequestId,false,null,"InvalidRequest");}
        catch(InvalidOperationException){return new(request.RequestId,false,null,"Denied");}
        catch(UnauthorizedAccessException){return new(request.RequestId,false,null,"Denied");}
        catch(SqlException){return new(request.RequestId,false,null,"StoreUnavailable");}
    }
}
