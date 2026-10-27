using Microsoft.Data.SqlClient;
using PharmaLIMS.TrustedSigning;
using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class TrustedWaterRegistrationStore(string connectionString)
{
    internal async Task<WaterRegistrationResult> ExecuteAsync(TrustedApplicationRequest envelope,
        byte[] version,string sid,CancellationToken cancellationToken)
    {
        WaterRegistrationPolicy.RequireTarget(connectionString);
        if(string.IsNullOrWhiteSpace(envelope.Reason) || envelope.Reason.Length>500 ||
            string.IsNullOrWhiteSpace(envelope.Meaning) || envelope.Meaning.Length>200 || version.Length!=8)
            throw new ArgumentException("Fresh Water signing reason, meaning and authentication version are required.");
        var input=JsonSerializer.Deserialize<WaterRegistrationRequest>(envelope.UserJson,
            new JsonSerializerOptions { UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow })
            ?? throw new ArgumentException("Missing Water registration.");
        string payload=WaterRegistrationPolicy.CanonicalPayload(input);
        string action=envelope.Action switch
        {
            "WaterRegister"=>"Register", "WaterResolve"=>"Resolve", "WaterCancel"=>"Cancel", "WaterLabel"=>"Label",
            _=>throw new ArgumentException("Unknown Water operation.")
        };
        await using var con=new SqlConnection(connectionString);
        await con.OpenAsync(cancellationToken);
        await using var tx=(SqlTransaction)await con.BeginTransactionAsync(IsolationLevel.Serializable,cancellationToken);
        using (var options=new SqlCommand("SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET CONCAT_NULL_YIELDS_NULL ON; SET ARITHABORT ON; SET NUMERIC_ROUNDABORT OFF;",con,tx))
            await options.ExecuteNonQueryAsync(cancellationToken);
        using var cmd=new SqlCommand("dbo.EQ001_UAT_WaterRegistration",con,tx){CommandType=CommandType.StoredProcedure,CommandTimeout=15};
        cmd.Parameters.Add("@Action",SqlDbType.NVarChar,16).Value=action;
        cmd.Parameters.Add("@RequestId",SqlDbType.UniqueIdentifier).Value=envelope.RequestId;
        cmd.Parameters.Add("@Payload",SqlDbType.NVarChar,-1).Value=payload;
        cmd.Parameters.Add("@Actor",SqlDbType.NVarChar,100).Value=envelope.Username;
        cmd.Parameters.Add("@ActorVersion",SqlDbType.Binary,8).Value=version;
        cmd.Parameters.Add("@CallerSid",SqlDbType.NVarChar,184).Value=sid;
        cmd.Parameters.Add("@Reason",SqlDbType.NVarChar,500).Value=envelope.Reason;
        cmd.Parameters.Add("@Meaning",SqlDbType.NVarChar,200).Value=envelope.Meaning;
        WaterRegistrationResult? result=null;
        if(action=="Register")
        {
            cmd.Parameters["@Action"].Value="Resolve";
            result=JsonSerializer.Deserialize<WaterRegistrationResult>((string)(await cmd.ExecuteScalarAsync(cancellationToken))!);
            if(result?.State=="Pending")
            {
                cmd.Parameters["@Action"].Value="ReadSpecs";
                using(var reader=await cmd.ExecuteReaderAsync(cancellationToken))
                    while(await reader.ReadAsync(cancellationToken))
                        WaterRegistrationPolicy.ValidateSnapshot(reader.GetString(1),reader.GetString(3),reader.GetString(6),
                            reader.IsDBNull(4)?null:reader.GetDecimal(4),reader.IsDBNull(5)?null:reader.GetDecimal(5));
                result=null;
            }
        }
        if(result is null)
        {
            cmd.Parameters["@Action"].Value=action;
            result=JsonSerializer.Deserialize<WaterRegistrationResult>((string)(await cmd.ExecuteScalarAsync(cancellationToken))!);
        }
        if(result is null || result.RequestId!=envelope.RequestId) throw new InvalidOperationException("Invalid Water commit response.");
        await tx.CommitAsync(cancellationToken);
        return result;
    }
}
