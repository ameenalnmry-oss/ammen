using PharmaLIMS.Models;
using PharmaLIMS.TrustedSigning;

namespace PharmaLIMS.Infrastructure
{
    internal sealed class DatabaseConnection(string value) { internal string Value=>value; }
    internal static class ApplicationLogger { internal static void Error(string message,Exception error){} }
}
namespace PharmaLIMS.Repositories
{
    internal sealed class UserRepository(PharmaLIMS.Infrastructure.DatabaseConnection database)
    {
        internal static User Actor {get;}=new(){Username="qa",Role="QA",CanApproveResults=true,AuthenticationRowVersion=new byte[8]};
        internal Task<User?> GetByUsernameAsync(string username,CancellationToken token){_=database;return Task.FromResult<User?>(Actor);}
    }
}
namespace PharmaLIMS.Services
{
    internal sealed class AuthService(PharmaLIMS.Repositories.UserRepository repository,bool useTrustedTransport)
    {
        internal Task<User?> AuthenticateAsync(string username,string password,CancellationToken token){_=repository;_=useTrustedTransport;return Task.FromResult<User?>(PharmaLIMS.Repositories.UserRepository.Actor);}
        internal Task<bool> ChangeCurrentUserPasswordAsync(string password,string next,CancellationToken token)=>Task.FromResult(true);
        internal User GetCurrentUser()=>PharmaLIMS.Repositories.UserRepository.Actor;
    }
    internal sealed class UserAdministrationService(PharmaLIMS.Infrastructure.DatabaseConnection database,bool useTrustedTransport)
    {
        internal Task<int> CreateUserAsync(User user,string password,string actor,string reason,string meaning,string signer,CancellationToken token){_=database;_=useTrustedTransport;throw new Exception("Unexpected admin");}
        internal Task UpdateUserAsync(User user,string actor,string reason,string meaning,string signer,CancellationToken token)=>throw new Exception("Unexpected admin");
        internal Task ResetPasswordAsync(int id,string password,string actor,string reason,string meaning,string signer,byte[] version,CancellationToken token)=>throw new Exception("Unexpected admin");
        internal Task UnlockUserAsync(int id,string actor,string reason,string meaning,string signer,byte[] version,CancellationToken token)=>throw new Exception("Unexpected admin");
    }
}
internal sealed class TrustedPrmSpecificationStore(string connection)
{
    internal Task<int> CommitAsync(TrustedApplicationRequest request,byte[] version,string sid,CancellationToken token){_=connection;throw new Exception("Unexpected PRM");}
}
internal sealed class TrustedWaterRegistrationStore(string connection)
{
    internal Task<WaterRegistrationResult> ExecuteAsync(TrustedApplicationRequest request,byte[] version,string sid,CancellationToken token){_=connection;throw new Exception("Unexpected registration");}
}
internal sealed class TrustedWaterWorkflowStore(string connection)
{
    internal static int Calls;
    internal Task<WaterWorkflowResult> ExecuteAsync(TrustedApplicationRequest request,byte[] version,string sid,CancellationToken token)
    {
        _=connection;Calls++;return Task.FromResult(new WaterWorkflowResult(request.RequestId,"Committed",1,"sample","Approved",1,1,0,""));
    }
}

internal sealed class TrustedPrmRegistrationStore(string connection)
{
    internal Task<PrmRegistrationResult> ExecuteAsync(TrustedApplicationRequest request,byte[] version,string sid,CancellationToken token)
    { _=connection; return Task.FromResult(new PrmRegistrationResult(request.RequestId,"Committed",1,"RM-2026-001","Registered",2)); }
}
