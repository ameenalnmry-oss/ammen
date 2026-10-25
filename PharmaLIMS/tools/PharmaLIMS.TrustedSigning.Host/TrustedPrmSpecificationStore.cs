using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class TrustedPrmSpecificationStore(string connectionString)
{
    internal async Task<int> CommitAsync(TrustedApplicationRequest envelope, byte[] authenticationVersion, string callerSid, CancellationToken cancellationToken)
    {
        var target=new SqlConnectionStringBuilder(connectionString);
        if(!target.InitialCatalog.StartsWith("EQ001_UAT_",StringComparison.OrdinalIgnoreCase) || !target.IntegratedSecurity || target.Encrypt == SqlConnectionEncryptOption.Optional || target.TrustServerCertificate)
            throw new UnauthorizedAccessException("Validated Windows-authenticated isolated UAT is required.");
        var request=JsonSerializer.Deserialize<PrmSpecificationRequest>(envelope.UserJson,
            new JsonSerializerOptions { UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow }) ?? throw new ArgumentException("Missing specification.");
        PrmSpecificationPolicy.Validate(request,envelope.Action);
        ValidateCriteria(request.Tests);
        await using var connection=new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction=(SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,cancellationToken);
        using(var settings=new SqlCommand("SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET CONCAT_NULL_YIELDS_NULL ON; SET ARITHABORT ON; SET NUMERIC_ROUNDABORT OFF;",connection,transaction))
            await settings.ExecuteNonQueryAsync(cancellationToken);
        if(request.Version>0)
        {
            using var load=new SqlCommand("dbo.EQ001_UAT_ReadPrmSpecification",connection,transaction){CommandType=CommandType.StoredProcedure,CommandTimeout=15};
            load.Parameters.Add("@No",SqlDbType.NVarChar,120).Value=request.SpecificationNo;
            load.Parameters.Add("@Category",SqlDbType.NVarChar,40).Value=request.Category;
            load.Parameters.Add("@Version",SqlDbType.Int).Value=request.Version;
            using var rows=new DataTable();
            using(var reader=await load.ExecuteReaderAsync(cancellationToken)) rows.Load(reader);
            if(rows.Rows.Count==0 || PrmSpecificationPolicy.SnapshotHash(rows)!=request.ExpectedSnapshotHash)
                throw new DBConcurrencyException("The complete specification snapshot changed. Reload before continuing.");
            if(envelope.Action!="PrmSaveDraft")
            {
                var stored=rows.Rows.Cast<DataRow>().Select(row=>new PrmSpecificationTest(
                    Convert.ToString(row["TestCode"],CultureInfo.InvariantCulture)!,Convert.ToString(row["TestName"],CultureInfo.InvariantCulture)!,
                    Convert.ToString(row["SpecificationText"],CultureInfo.InvariantCulture)!,row["SpecificationLimit"] is DBNull?null:Convert.ToDecimal(row["SpecificationLimit"],CultureInfo.InvariantCulture),
                    Convert.ToBoolean(row["IsDefaultForCategory"],CultureInfo.InvariantCulture),row["Unit"] is DBNull?"":Convert.ToString(row["Unit"],CultureInfo.InvariantCulture)!,
                    Convert.ToString(row["ResultType"],CultureInfo.InvariantCulture)!,Convert.ToBoolean(row["RequiredTest"],CultureInfo.InvariantCulture),
                    row["MinimumElapsedHours"] is DBNull?0:Convert.ToDecimal(row["MinimumElapsedHours"],CultureInfo.InvariantCulture),
                    row["SortOrder"] is DBNull?0:Convert.ToInt32(row["SortOrder"],CultureInfo.InvariantCulture))).ToArray();
                PrmSpecificationPolicy.Validate(request with { Tests=stored },envelope.Action);
                ValidateCriteria(stored);
            }
        }
        using var write=new SqlCommand("dbo.EQ001_UAT_WritePrmSpecification",connection,transaction){CommandType=CommandType.StoredProcedure,CommandTimeout=15};
        write.Parameters.Add("@Action",SqlDbType.NVarChar,30).Value=envelope.Action;
        write.Parameters.Add("@Payload",SqlDbType.NVarChar,-1).Value=JsonSerializer.Serialize(request);
        write.Parameters.Add("@Actor",SqlDbType.NVarChar,100).Value=envelope.Username;
        write.Parameters.Add("@CallerSid",SqlDbType.NVarChar,184).Value=callerSid;
        write.Parameters.Add("@RequestId",SqlDbType.UniqueIdentifier).Value=envelope.RequestId;
        write.Parameters.Add("@ActorVersion",SqlDbType.Binary,8).Value=authenticationVersion;
        write.Parameters.Add("@Reason",SqlDbType.NVarChar,500).Value=envelope.Reason;
        write.Parameters.Add("@Meaning",SqlDbType.NVarChar,250).Value=envelope.Meaning;
        int version=Convert.ToInt32(await write.ExecuteScalarAsync(cancellationToken),CultureInfo.InvariantCulture);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return version;
    }

    private static void ValidateCriteria(IEnumerable<PrmSpecificationTest> tests)
    {
        foreach(var test in tests)
        {
            if(test.ResultType=="Numeric" && PrmNumericSpecificationEvaluator.Evaluate(0m,test.SpecificationText,
                test.SpecificationLimit?.ToString(CultureInfo.InvariantCulture),test.TestCode,test.TestName)=="Check Required")
                throw new ArgumentException("Numeric specification criteria and limit are inconsistent.");
            if(test.ResultType=="Qualitative" && !PrmResultInterpretationEvaluator.TryGetRequiredPresence(test.SpecificationText,out _))
                throw new ArgumentException("Qualitative criteria must unambiguously require Present or Absent.");
        }
    }
}
