using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;
using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class TrustedWaterWorkflowStore(string connectionString)
{
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive=false,
        MaxDepth=16
    };

    internal async Task<WaterWorkflowResult> ExecuteAsync(TrustedApplicationRequest envelope,
        byte[] version,string sid,CancellationToken cancellationToken)
    {
        if(envelope.RequestId==Guid.Empty || string.IsNullOrWhiteSpace(envelope.Username) ||
            string.IsNullOrWhiteSpace(sid) || sid.Length>184 || version.Length!=8 ||
            string.IsNullOrWhiteSpace(envelope.Reason) || envelope.Reason.Length>500 ||
            string.IsNullOrWhiteSpace(envelope.Meaning) || envelope.Meaning.Length>200 ||
            envelope.UserJson.Length>32768)
            throw new ArgumentException("A fresh native Water signing identity, reason and meaning are required.");
        string mode=envelope.Action switch
        {
            "WaterWorkflow"=>"Commit", "WaterWorkflowResolve"=>"Resolve", "WaterWorkflowCancel"=>"Cancel",
            _=>throw new ArgumentException("Unknown Water workflow action.")
        };
        using(var document=JsonDocument.Parse(envelope.UserJson,new JsonDocumentOptions{MaxDepth=16}))
            RequireUniqueProperties(document.RootElement);
        var input=JsonSerializer.Deserialize<WaterWorkflowRequest>(envelope.UserJson,StrictJson)
            ??throw new ArgumentException("Missing Water workflow input.");
        WaterWorkflowPolicy.RequireTarget(connectionString,input);
        string payload=WaterWorkflowPolicy.CanonicalPayload(input);

        var boundedConnection=new SqlConnectionStringBuilder(connectionString);
        if(boundedConnection.ConnectTimeout==0 || boundedConnection.ConnectTimeout>15)boundedConnection.ConnectTimeout=15;
        await using var connection=new SqlConnection(boundedConnection.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction=(SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,cancellationToken);
        using(var options=new SqlCommand("SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET CONCAT_NULL_YIELDS_NULL ON; SET ARITHABORT ON; SET NUMERIC_ROUNDABORT OFF; SET LOCK_TIMEOUT 10000;",connection,transaction){CommandTimeout=15})
            await options.ExecuteNonQueryAsync(cancellationToken);
        using var command=new SqlCommand("dbo.EQ001_UAT_WaterWorkflow",connection,transaction)
        {
            CommandType=CommandType.StoredProcedure, CommandTimeout=15
        };
        command.Parameters.Add("@Mode",SqlDbType.NVarChar,16).Value="Resolve";
        command.Parameters.Add("@Operation",SqlDbType.NVarChar,32).Value=input.Operation;
        command.Parameters.Add("@RequestId",SqlDbType.UniqueIdentifier).Value=envelope.RequestId;
        command.Parameters.Add("@SampleId",SqlDbType.Int).Value=input.SampleId;
        command.Parameters.Add("@Payload",SqlDbType.NVarChar,-1).Value=payload;
        command.Parameters.Add("@Plan",SqlDbType.NVarChar,-1).Value=DBNull.Value;
        command.Parameters.Add("@Actor",SqlDbType.NVarChar,100).Value=envelope.Username;
        command.Parameters.Add("@ActorVersion",SqlDbType.Binary,8).Value=version;
        command.Parameters.Add("@CallerSid",SqlDbType.NVarChar,184).Value=sid;
        command.Parameters.Add("@Reason",SqlDbType.NVarChar,500).Value=envelope.Reason;
        command.Parameters.Add("@Meaning",SqlDbType.NVarChar,200).Value=envelope.Meaning;

        // Historical receipt recovery happens before stale status/snapshot checks. Current
        // active actor/version/SID are still verified independently by the owner procedure.
        var outcome=await OutcomeAsync(command,envelope.RequestId,input,cancellationToken);
        if(outcome.State=="Pending" && mode=="Commit")
        {
            command.Parameters["@Mode"].Value="Read";
            WaterWorkflowSnapshot snapshot;
            using var cancelRead=cancellationToken.Register(command.Cancel);
            try
            {
                await using var reader=await command.ExecuteReaderAsync(cancellationToken);
                snapshot=WaterWorkflowSnapshot.FromReader(reader);
            }
            catch(SqlException) when(cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if(!string.Equals(snapshot.Hash,input.ExpectedSnapshot,StringComparison.Ordinal))
                throw new DBConcurrencyException("Water workflow snapshot changed.");
            var plan=WaterWorkflowValidator.Build(snapshot,input);
            command.Parameters["@Mode"].Value="Commit";
            command.Parameters["@Plan"].Value=JsonSerializer.Serialize(plan);
            // Commit checks the exact persisted result after triggers, then writes its
            // receipt. The retained transaction rolls back all effects if any check fails.
            outcome=await OutcomeAsync(command,envelope.RequestId,input,cancellationToken);
            if(outcome.State=="Pending")throw new InvalidOperationException("Water workflow commit remained pending.");
        }
        else if(outcome.State=="Pending" && mode=="Cancel")
        {
            command.Parameters["@Mode"].Value="Cancel";
            outcome=await OutcomeAsync(command,envelope.RequestId,input,cancellationToken);
            if(outcome.State=="Pending")throw new InvalidOperationException("Water workflow cancellation remained pending.");
        }
        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    private static async Task<WaterWorkflowResult> OutcomeAsync(SqlCommand command,Guid requestId,
        WaterWorkflowRequest input,CancellationToken cancellationToken)
    {
        var value=await command.ExecuteScalarAsync(cancellationToken);
        var outcome=value is string json ? JsonSerializer.Deserialize<WaterWorkflowResult>(json,StrictJson) : null;
        if(outcome is null || outcome.RequestId!=requestId || outcome.SampleId!=input.SampleId ||
            outcome.SampleNumber!=input.SampleNumber || outcome.State is not ("Pending" or "Committed" or "Cancelled"))
            throw new InvalidOperationException("Invalid Water workflow receipt response.");
        return outcome;
    }

    private static void RequireUniqueProperties(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in element.EnumerateObject())
            {
                if(!names.Add(property.Name))throw new JsonException("Duplicate Water workflow property.");
                RequireUniqueProperties(property.Value);
            }
        }
        else if(element.ValueKind==JsonValueKind.Array)
            foreach(var item in element.EnumerateArray())RequireUniqueProperties(item);
    }
}
