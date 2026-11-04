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

    public static async Task<int> PrmAsync(string action, PrmSpecificationRequest specification, string actor,
        string reason, string meaning, CancellationToken cancellationToken = default)
    {
        PrmSpecificationPolicy.Validate(specification,action);
        string ticket=_ticket; _ticket="";
        if(string.IsNullOrEmpty(ticket)) throw new UnauthorizedAccessException("Fresh trusted reauthentication is required for PRM changes.");
        var request=new TrustedApplicationRequest(Guid.NewGuid(),action,actor,Ticket:ticket,
            UserJson:JsonSerializer.Serialize(specification),Reason:reason,Meaning:meaning);
        try
        {
            var reply=await TrustedEquipmentSigningClient.CreateFromConfiguration().ApplicationAsync(request,cancellationToken);
            if(!string.Equals(reply.SignedBy,actor,StringComparison.OrdinalIgnoreCase) || !int.TryParse(reply.UserJson,out int version) || version<=0)
                throw new InvalidOperationException("Invalid trusted PRM response.");
            return version;
        }
        catch(TrustedSigningRejectedException ex) when(ex.ErrorCode=="HistoricalVersionPreserved")
        { throw new InvalidOperationException("The existing approved version was preserved. Activation requires a separately controlled supersession decision.",ex); }
        catch(TrustedSigningRejectedException ex) when(ex.ErrorCode=="PrmSnapshotChanged")
        { throw new InvalidOperationException("The complete specification changed. Reload before signing; no changes were committed.",ex); }
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

    public static async Task<WaterRegistrationResult> WaterAsync(string action,Guid requestId,
        WaterRegistrationRequest payload,string actor,string reason,string meaning,CancellationToken cancellationToken=default)
    {
        if(action is not ("WaterRegister" or "WaterResolve" or "WaterCancel" or "WaterLabel") || requestId==Guid.Empty)
            throw new ArgumentException("Invalid Water operation.");
        WaterRegistrationPolicy.RequireTarget(AppConfig.ConnectionString);
        string canonical=WaterRegistrationPolicy.CanonicalPayload(payload);
        string ticket=_ticket; _ticket="";
        if(string.IsNullOrEmpty(ticket)) throw new UnauthorizedAccessException("Fresh trusted reauthentication is required for Water registration.");
        var reply=await TrustedEquipmentSigningClient.CreateFromConfiguration().ApplicationAsync(
            new(requestId,action,actor,Ticket:ticket,UserJson:canonical,Reason:reason,Meaning:meaning),cancellationToken);
        var result=reply.UserJson is null?null:JsonSerializer.Deserialize<WaterRegistrationResult>(reply.UserJson);
        if(!string.Equals(reply.SignedBy,actor,StringComparison.OrdinalIgnoreCase) || result is null || result.RequestId!=requestId ||
            result.State is not ("Pending" or "Cancelled" or "Committed") ||
            result.State=="Committed" && (result.SampleId<=0 || string.IsNullOrWhiteSpace(result.SampleNumber) || result.TestCount!=payload.TestIds.Length))
            throw new InvalidOperationException("Invalid trusted Water response; recover the same request before registering again.");
        return result;
    }
    public static bool IsPrmRegistrationConfigured => AppConfig.TrustedSigningEnabled;

    internal static void RequirePrmRegistrationFrame(PrmRegistrationRequest payload, string actor, string reason, string meaning)
    {
        var envelope=new TrustedSigningEnvelope(null,"","",new TrustedApplicationRequest(Guid.Empty,
            "PrmRegister",actor,Ticket:_ticket,UserJson:PrmRegistrationPolicy.CanonicalPayload(payload),Reason:reason,Meaning:meaning));
        if(JsonSerializer.SerializeToUtf8Bytes(envelope).Length>TrustedPipeProtocol.MaximumFrameBytes)
            throw new ArgumentException("PRM registration exceeds the 64 KB transport frame; reduce the input before signing. No payload was truncated.");
    }

    public static async Task<PrmRegistrationResult> PrmRegistrationAsync(string action, Guid requestId,
        PrmRegistrationRequest payload, string actor, string reason, string meaning, CancellationToken cancellationToken = default)
    {
        if(action is not ("PrmRegister" or "PrmResolve" or "PrmCancel") || requestId==Guid.Empty)
            throw new ArgumentException("Invalid PRM registration action.");
        PrmRegistrationPolicy.RequireTarget(AppConfig.ConnectionString, requireIntegratedSecurity:false);
        string canonical=PrmRegistrationPolicy.CanonicalPayload(payload);
        string ticket=_ticket; _ticket="";
        if(string.IsNullOrEmpty(ticket)) throw new UnauthorizedAccessException("Fresh trusted reauthentication is required for PRM registration.");
        try
        {
            var reply=await TrustedEquipmentSigningClient.CreateFromConfiguration().ApplicationAsync(
                new(requestId,action,actor,Ticket:ticket,UserJson:canonical,Reason:reason,Meaning:meaning),cancellationToken);
            var result=reply.UserJson is null?null:JsonSerializer.Deserialize<PrmRegistrationResult>(reply.UserJson,
                new JsonSerializerOptions { UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
            if(!string.Equals(reply.SignedBy,actor,StringComparison.OrdinalIgnoreCase) || result is null || result.RequestId!=requestId ||
                result.State is not ("Pending" or "Cancelled" or "Committed") || result.State=="Committed" &&
                (result.SampleId<=0 || result.TestCount<=0 || string.IsNullOrWhiteSpace(result.SampleNumber) || result.Status!="Registered"))
                throw new InvalidOperationException("Invalid trusted PRM response. Recover the same saved request before registering again.");
            return result;
        }
        catch(TrustedSigningRejectedException ex) when(ex.ErrorCode=="PrmRegistrationDisabled")
        { throw new InvalidOperationException("Trusted PRM registration is not enabled for this isolated UAT deployment. The saved request remains available for recovery.",ex); }
        catch(TrustedSigningRejectedException ex) when(ex.ErrorCode=="PrmRegistrationStateRejected")
        { throw new InvalidOperationException("The PRM registration state was rejected. Recover or cancel the same saved request before preparing another registration.",ex); }
    }

    internal static void RequireWaterWorkflowFrame(WaterWorkflowRequest payload, string actor, string reason, string meaning)
    {
        // Reserve the longest recovery action, so a permitted write can always be cancelled with the same payload.
        var envelope = new TrustedSigningEnvelope(null, "", "", new TrustedApplicationRequest(Guid.Empty,
            "WaterWorkflowCancel", actor, Ticket: _ticket, UserJson: WaterWorkflowPolicy.CanonicalPayload(payload), Reason: reason, Meaning: meaning));
        if (JsonSerializer.SerializeToUtf8Bytes(envelope).Length > TrustedPipeProtocol.MaximumFrameBytes)
            throw new ArgumentException("Water workflow exceeds the 64 KB transport frame. Reduce the input before signing; no payload was truncated.");
    }

    public static async Task<WaterWorkflowResult> WaterWorkflowAsync(string action, Guid requestId,
        WaterWorkflowRequest payload, string actor, string reason, string meaning, CancellationToken cancellationToken = default)
    {
        if (action is not ("WaterWorkflow" or "WaterWorkflowResolve" or "WaterWorkflowCancel") || requestId == Guid.Empty)
            throw new ArgumentException("Invalid Water workflow action.");
        WaterWorkflowPolicy.RequireTarget(AppConfig.ConnectionString, payload, requireIntegratedSecurity: false);
        string canonical = WaterWorkflowPolicy.CanonicalPayload(payload);
        string ticket = _ticket; _ticket = "";
        if (string.IsNullOrEmpty(ticket))
            throw new UnauthorizedAccessException("Fresh trusted reauthentication is required for Water analysis.");
        try
        {
            var reply = await TrustedEquipmentSigningClient.CreateFromConfiguration().ApplicationAsync(
                new(requestId, action, actor, Ticket: ticket, UserJson: canonical, Reason: reason, Meaning: meaning), cancellationToken);
            var result = reply.UserJson is null ? null : JsonSerializer.Deserialize<WaterWorkflowResult>(reply.UserJson,
                new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
            if (!string.Equals(reply.SignedBy, actor, StringComparison.OrdinalIgnoreCase) || result is null ||
                result.RequestId != requestId || result.SampleId != payload.SampleId ||
                !string.Equals(result.SampleNumber, payload.SampleNumber, StringComparison.Ordinal) ||
                result.State is not ("Pending" or "Cancelled" or "Committed") || result.CompletedTests < 0 || result.PendingTests < 0 ||
                result.State == "Committed" && (result.SignatureId <= 0 || !ValidWorkflowStatus(payload, result.Status)))
                throw new InvalidOperationException("Invalid trusted Water workflow response. Recover the same saved request before continuing.");
            return result;
        }
        catch (TrustedSigningRejectedException ex) when (ex.ErrorCode is "WorkflowDisabled" or "WaterWorkflowDisabled")
        { throw new InvalidOperationException("Trusted Water analysis is not enabled for this deployment. The saved request remains available for recovery; contact the deployment administrator.", ex); }
        catch (TrustedSigningRejectedException ex) when (ex.ErrorCode is "SnapshotConflict" or "WaterWorkflowSnapshotChanged")
        { throw new InvalidOperationException("The Water sample or its evidence changed. The saved request was retained. Recover or cancel it before reloading and signing a new request.", ex); }
        catch (TrustedSigningRejectedException ex) when (ex.ErrorCode is "StateRejected" or "WaterWorkflowStateRejected")
        { throw new InvalidOperationException("The service rejected this Water workflow state. The saved request was retained; recover or cancel the same request before preparing a replacement.", ex); }
    }

    private static bool ValidWorkflowStatus(WaterWorkflowRequest payload, string status) => payload.Operation switch
    {
        "StartAnalysis" => status == "In Analysis",
        "SubmitReview" => status == "Under Review",
        "Review" => status == "Reviewed",
        "Approve" => status == "Approved",
        "CorrectTimes" => status == payload.ExpectedStatus,
        "SaveResults" => status is "In Analysis" or "Partially Entered" or "Results Entered" or "Alert" or "OOS" or
            "Under Investigation" or "Released After Investigation",
        _ => false
    };

}
