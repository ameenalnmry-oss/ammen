using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Models;
using PharmaLIMS.Repositories;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

internal sealed class TrustedApplicationEndpoint(string connectionString, bool enablePrmUat = false, bool enableWaterUat = false, bool enableWaterAnalysisUat = false, bool enablePrmRegistrationUat = false, bool enableEquipmentDraftUat = false, bool enableEquipmentLinkUat = false, bool enableEquipmentConfirmUat = false) : ITrustedApplicationEndpoint
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
            if(request.Action is "Login" or "Reauthenticate" or "ActivityReauthenticate" or "ChangePassword")
            {
                var auth = new AuthService(repository, useTrustedTransport:false);
                User? user = request.Action=="ActivityReauthenticate"
                    ? await auth.ReauthenticateForTrustedActionAsync(request.Username,request.Password,cancellationToken)
                    : await auth.AuthenticateAsync(request.Username,request.Password,cancellationToken);
                if(user is null)
                {
                    var state = await repository.GetByUsernameAsync(request.Username,cancellationToken);
                    return new(request.RequestId,false,null,state?.IsLocked==true?"Locked":state?.IsActive==false?"Inactive":"CredentialsRejected");
                }
                using var authenticatedAuditActor = TrustedAuditActorContext.Enter(user.Username);
                if(request.Action=="ChangePassword")
                {
                    if(!await auth.ChangeCurrentUserPasswordAsync(request.Password,request.NewPassword,cancellationToken))return new(request.RequestId,false,null,"CredentialsRejected");
                    user=auth.GetCurrentUser()!;
                }
                if((request.Action is "Reauthenticate" or "ActivityReauthenticate") && user.MustChangePassword)return new(request.RequestId,false,null,"Inactive");
                if(request.Action=="ActivityReauthenticate" && user.PasswordChangedAt is null)return new(request.RequestId,false,null,"CredentialNotInitialized");
                string? ticket=null;
                if(request.Action=="Reauthenticate")
                {
                    ticket=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    _tickets[ticket]=new(sid,user.Username,DateTimeOffset.UtcNow.AddSeconds(30),user.AuthenticationRowVersion);
                    foreach(var entry in _tickets)if(entry.Value.Expires<DateTimeOffset.UtcNow)_tickets.TryRemove(entry.Key,out _);
                }
                user.ClearSensitivePasswordData();
                // User.AuthenticationRowVersion intentionally stays internal and
                // is omitted by ordinary User serialization. The host-to-host
                // activity proof must explicitly carry the committed version;
                // it carries no password fields or desktop authorization ticket.
                string userJson=request.Action=="ActivityReauthenticate"
                    ? JsonSerializer.Serialize(new VerifiedActivitySigner(user.Username,user.IsActive,user.IsLocked,
                        user.MustChangePassword,user.CanReviewResults,user.CanApproveResults,
                        (byte[])user.AuthenticationRowVersion.Clone()))
                    : JsonSerializer.Serialize(user);
                return new(request.RequestId,true,user.Username,"",userJson,ticket);
            }
            bool equipmentConfirm=request.Action=="EquipmentConfirm";
            if(equipmentConfirm && !enableEquipmentConfirmUat) return new(request.RequestId,false,null,"EquipmentConfirmDisabled");
            bool equipmentLink=request.Action=="EquipmentLink";
            if(equipmentLink && !enableEquipmentLinkUat) return new(request.RequestId,false,null,"EquipmentLinkDisabled");
            bool equipmentDraft=request.Action=="EquipmentDraft";
            if(equipmentDraft && !enableEquipmentDraftUat) return new(request.RequestId,false,null,"EquipmentDraftDisabled");
            bool prm=request.Action is "PrmSaveDraft" or "PrmReview" or "PrmApprove";
            bool water=request.Action is "WaterRegister" or "WaterResolve" or "WaterCancel" or "WaterLabel";
            bool prmRegistration=request.Action is "PrmRegister" or "PrmResolve" or "PrmCancel";
            if(prmRegistration && !enablePrmRegistrationUat) return new(request.RequestId,false,null,"PrmRegistrationDisabled");
            bool workflow=request.Action is "WaterWorkflow" or "WaterWorkflowResolve" or "WaterWorkflowCancel";
            if(workflow && !enableWaterAnalysisUat) return new(request.RequestId,false,null,"WaterWorkflowDisabled");
            if(water && !enableWaterUat) return new(request.RequestId,false,null,"WaterDisabled");
            if(prm && !enablePrmUat) return new(request.RequestId,false,null,"Denied");
            if(!prm && !water && !workflow && !prmRegistration && !equipmentDraft && !equipmentLink && !equipmentConfirm && request.Action is not ("CreateUser" or "UpdateUser" or "ResetPassword" or "UnlockUser"))return new(request.RequestId,false,null,"InvalidRequest");
            if(!_tickets.TryRemove(request.Ticket,out var proof) || proof.Sid!=sid || !proof.Username.Equals(request.Username,StringComparison.OrdinalIgnoreCase) || proof.Expires<DateTimeOffset.UtcNow)
                return new(request.RequestId,false,null,"Denied");
            var actor=await repository.GetByUsernameAsync(proof.Username,cancellationToken);
            if(actor is null || !actor.IsActive || actor.IsLocked || actor.MustChangePassword || (!prm && !water && !workflow && !prmRegistration && !equipmentDraft && !equipmentLink && !equipmentConfirm && !actor.CanManageUsers) || ((water || prmRegistration) && !actor.CanRegisterSamples) || ((equipmentDraft || equipmentLink || equipmentConfirm) && !actor.CanEnterResults) || !actor.AuthenticationRowVersion.SequenceEqual(proof.Version))return new(request.RequestId,false,null,"Denied");
            using var authorizedAuditActor = TrustedAuditActorContext.Enter(actor.Username);
            if(equipmentConfirm)
            {
                EquipmentConfirmReceipt receipt=await new TrustedEquipmentConfirmStore(connectionString).ExecuteAsync(
                    request with { Username=proof.Username },proof.Version,proof.Sid,cancellationToken);
                return new(request.RequestId,true,proof.Username,"",JsonSerializer.Serialize(receipt));
            }
            if(equipmentLink)
            {
                EquipmentLinkReceipt receipt=await new TrustedEquipmentLinkStore(connectionString).ExecuteAsync(
                    request with { Username=proof.Username },proof.Version,proof.Sid,cancellationToken);
                return new(request.RequestId,true,proof.Username,"",JsonSerializer.Serialize(receipt));
            }
            if(equipmentDraft)
            {
                EquipmentDraftReceipt receipt=await new TrustedEquipmentDraftStore(connectionString).ExecuteAsync(
                    request with { Username=proof.Username },proof.Version,proof.Sid,cancellationToken);
                return new(request.RequestId,true,proof.Username,"",JsonSerializer.Serialize(receipt));
            }
            if(prmRegistration)
            {
                var committed=await new TrustedPrmRegistrationStore(connectionString).ExecuteAsync(
                    request with { Username=proof.Username },proof.Version,proof.Sid,cancellationToken);
                return new(request.RequestId,true,proof.Username,"",JsonSerializer.Serialize(committed));
            }
            if(workflow)
            {
                var committed=await new TrustedWaterWorkflowStore(connectionString).ExecuteAsync(
                    request with { Username=proof.Username },proof.Version,proof.Sid,cancellationToken);
                return new(request.RequestId,true,proof.Username,"",JsonSerializer.Serialize(committed));
            }
            if(water)
            {
                var committed=await new TrustedWaterRegistrationStore(connectionString).ExecuteAsync(
                    request with { Username=proof.Username },proof.Version,proof.Sid,cancellationToken);
                return new(request.RequestId,true,proof.Username,"",JsonSerializer.Serialize(committed));
            }
            if(prm)
            {
                int version=await new TrustedPrmSpecificationStore(connectionString).CommitAsync(
                    request with { Username=proof.Username },proof.Version,proof.Sid,cancellationToken);
                return new(request.RequestId,true,proof.Username,"",version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
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
        catch(InvalidOperationException ex) when(IsUserAdministrationAction(request.Action))
        {
            string code=ClassifyUserAdministrationInvalidOperation(ex.Message);
            ApplicationLogger.Warning(
                $"Trusted user administration rejected. Action='{request.Action}', Request='{request.RequestId:N}', Category='{code}'.");
            return new(request.RequestId,false,null,code);
        }
        catch(UnauthorizedAccessException ex) when(IsUserAdministrationAction(request.Action))
        {
            string code=ClassifyUserAdministrationAuthorization(ex.Message);
            ApplicationLogger.Warning(
                $"Trusted user administration authorization rejected. Action='{request.Action}', Request='{request.RequestId:N}', Category='{code}'.");
            return new(request.RequestId,false,null,code);
        }
        catch(InvalidOperationException){return new(request.RequestId,false,null,"Denied");}
        catch(UnauthorizedAccessException){return new(request.RequestId,false,null,"Denied");}
        catch(System.Data.DBConcurrencyException){return new(request.RequestId,false,null,request.Action.StartsWith("WaterWorkflow",StringComparison.Ordinal)?"WaterWorkflowSnapshotChanged":"PrmSnapshotChanged");}
        catch(SqlException ex) when(ex.Number is >=57301 and <=57412)
        {
            ApplicationLogger.Error("Trusted equipment workflow rejected; request " +
                request.RequestId.ToString("N") + ".", SafeSqlError(ex));
            return new(request.RequestId,false,null,"EquipmentWorkflowStateRejected");
        }
        catch(SqlException ex) when(ex.Number is >=57201 and <=57212)
        {
            ApplicationLogger.Error("Trusted equipment draft rejected; request " +
                request.RequestId.ToString("N") + ".", SafeSqlError(ex));
            return new(request.RequestId,false,null,"EquipmentDraftStateRejected");
        }
        catch(SqlException ex) when(ex.Number is >=57000 and <=57049)
        {
            ApplicationLogger.Error("Trusted Water workflow rejected; request "+request.RequestId.ToString("N")+".",SafeSqlError(ex));
            return new(request.RequestId,false,null,ex.Number==57010?"WaterWorkflowSnapshotChanged":"WaterWorkflowStateRejected");
        }
        catch(SqlException ex) when(ex.Number is >=56930 and <=56940)
        {
            ApplicationLogger.Error("Trusted PRM registration rejected; request "+request.RequestId.ToString("N")+".",SafeSqlError(ex));
            return new(request.RequestId,false,null,"PrmRegistrationStateRejected");
        }
        catch(SqlException ex) when(ex.Number==56806){return new(request.RequestId,false,null,"HistoricalVersionPreserved");}
        catch(SqlException ex) when(ex.Number is >=56800 and <=56808){return new(request.RequestId,false,null,"PrmStateRejected");}
        catch(SqlException ex) when(ex.Number is >=56900 and <=56916)
        {
            ApplicationLogger.Error("Trusted Water operation rejected; request "+request.RequestId.ToString("N")+".",ex);
            return new(request.RequestId,false,null,"WaterStateRejected");
        }
        catch(SqlException ex)
        {
            if(request.Action.StartsWith("Equipment",StringComparison.Ordinal))
                ApplicationLogger.Error("Trusted equipment store unavailable; request " +
                    request.RequestId.ToString("N") + ".",SafeSqlError(ex));
            if(request.Action.StartsWith("Prm",StringComparison.Ordinal))
                ApplicationLogger.Error("Trusted PRM store failed; request " + request.RequestId.ToString("N") + ".",ex);
            if(request.Action.StartsWith("WaterWorkflow",StringComparison.Ordinal))
                ApplicationLogger.Error("Trusted Water workflow failed; request " + request.RequestId.ToString("N") + ".",SafeSqlError(ex));
            else if(request.Action.StartsWith("Water",StringComparison.Ordinal))
                ApplicationLogger.Error("Trusted Water store failed; request " + request.RequestId.ToString("N") + ".",ex);
            return new(request.RequestId,false,null,"StoreUnavailable");
        }
    }

    private static bool IsUserAdministrationAction(string action) =>
        action is "CreateUser" or "UpdateUser" or "ResetPassword" or "UnlockUser";

    private static string ClassifyUserAdministrationInvalidOperation(string message)
    {
        string value=message??string.Empty;
        if(value.Contains("changed after it was loaded",StringComparison.OrdinalIgnoreCase) ||
           value.Contains("changed after it was loaded",StringComparison.OrdinalIgnoreCase) ||
           value.Contains("Refresh before",StringComparison.OrdinalIgnoreCase))
            return "AdminTargetChanged";
        if(value.Contains("own account",StringComparison.OrdinalIgnoreCase))
            return "AdminSelfResetDenied";
        if(value.Contains("password",StringComparison.OrdinalIgnoreCase))
            return "AdminPasswordPolicyRejected";
        if(value.Contains("justification",StringComparison.OrdinalIgnoreCase) ||
           value.Contains("reason",StringComparison.OrdinalIgnoreCase))
            return "AdminReasonRejected";
        if(value.Contains("audit actor",StringComparison.OrdinalIgnoreCase) ||
           value.Contains("authenticated user account",StringComparison.OrdinalIgnoreCase))
            return "AdminAuditActorRejected";
        return "AdminStateRejected";
    }

    private static string ClassifyUserAdministrationAuthorization(string message)
    {
        string value=message??string.Empty;
        if(value.Contains("signature",StringComparison.OrdinalIgnoreCase) ||
           value.Contains("signer",StringComparison.OrdinalIgnoreCase))
            return "AdminSignerMismatch";
        if(value.Contains("permission",StringComparison.OrdinalIgnoreCase) ||
           value.Contains("User Management",StringComparison.OrdinalIgnoreCase) ||
           value.Contains("authenticated user",StringComparison.OrdinalIgnoreCase))
            return "AdminPermissionDenied";
        if(value.Contains("audit actor",StringComparison.OrdinalIgnoreCase))
            return "AdminAuditActorRejected";
        return "AdminPermissionDenied";
    }

    // SQL diagnostics can contain quoted row values. Keep only nonclinical provider codes.
    private static Exception SafeSqlError(SqlException error) => new InvalidOperationException(
        $"SQL provider error {error.Number}; class {error.Class}; state {error.State}.");
}
