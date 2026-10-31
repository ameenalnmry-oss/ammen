using Microsoft.Data.SqlClient;
using PharmaLIMS.TrustedSigning;
using System.Data;
using System.Text.Json;
internal sealed class TrustedPrmRegistrationStore(string connectionString)
{
    internal async Task<PrmRegistrationResult> ExecuteAsync(TrustedApplicationRequest envelope,byte[] version,string sid,CancellationToken cancellationToken)
    {
        PrmRegistrationPolicy.RequireTarget(connectionString);
        if(envelope.RequestId==Guid.Empty || version.Length!=8 || string.IsNullOrWhiteSpace(sid) || sid.Length>184 ||
            string.IsNullOrWhiteSpace(envelope.Reason) || envelope.Reason.Length>500 || string.IsNullOrWhiteSpace(envelope.Meaning) || envelope.Meaning.Length>200)
            throw new ArgumentException("Fresh PRM authentication and a valid request identity are required.");
        var input=PrmRegistrationPolicy.Parse(envelope.UserJson);
        string action=envelope.Action switch { "PrmRegister"=>"Register","PrmResolve"=>"Resolve","PrmCancel"=>"Cancel",_=>throw new ArgumentException("Unknown PRM registration operation.") };
        await using var con=new SqlConnection(connectionString);
        await con.OpenAsync(cancellationToken);
        await using var tx=(SqlTransaction)await con.BeginTransactionAsync(IsolationLevel.Serializable,cancellationToken);
        using(var options=new SqlCommand("SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET CONCAT_NULL_YIELDS_NULL ON; SET ARITHABORT ON; SET NUMERIC_ROUNDABORT OFF;",con,tx))
            await options.ExecuteNonQueryAsync(cancellationToken);
        using var cmd=new SqlCommand("dbo.EQ001_UAT_PrmRegistration",con,tx) { CommandType=CommandType.StoredProcedure,CommandTimeout=15 };
        cmd.Parameters.Add("@Action",SqlDbType.NVarChar,16).Value=action;
        cmd.Parameters.Add("@RequestId",SqlDbType.UniqueIdentifier).Value=envelope.RequestId;
        cmd.Parameters.Add("@Payload",SqlDbType.NVarChar,-1).Value=PrmRegistrationPolicy.CanonicalPayload(input);
        cmd.Parameters.Add("@Actor",SqlDbType.NVarChar,100).Value=envelope.Username;
        cmd.Parameters.Add("@ActorVersion",SqlDbType.Binary,8).Value=version;
        cmd.Parameters.Add("@CallerSid",SqlDbType.NVarChar,184).Value=sid;
        cmd.Parameters.Add("@Reason",SqlDbType.NVarChar,500).Value=envelope.Reason;
        cmd.Parameters.Add("@Meaning",SqlDbType.NVarChar,200).Value=envelope.Meaning;
        var result=JsonSerializer.Deserialize<PrmRegistrationResult>((string)(await cmd.ExecuteScalarAsync(cancellationToken))!);
        if(result is null || result.RequestId!=envelope.RequestId || result.State is not ("Pending" or "Cancelled" or "Committed") ||
            (result.State=="Committed" && (result.SampleId<=0 || result.TestCount<=0 || string.IsNullOrWhiteSpace(result.SampleNumber) || result.Status!="Registered")))
            throw new InvalidOperationException("Invalid PRM commit response.");
        await tx.CommitAsync(cancellationToken);
        return result;
    }
}
