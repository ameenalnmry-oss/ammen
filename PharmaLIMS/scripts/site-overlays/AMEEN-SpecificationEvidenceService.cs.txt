using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
namespace PharmaLIMS.Services;
internal static class SpecificationEvidenceService
{
    private const string RowsSql=@"SELECT s.* FROM dbo.PRM_SpecificationTests s WITH(UPDLOCK,HOLDLOCK)
WHERE s.SpecificationNo=@No AND s.SampleCategory=@Category AND s.VersionNo=@Version ORDER BY s.SpecificationTestID";
    private static SqlCommand Command(SqlConnection c,SqlTransaction t,string sql,string no,string category,int version)
    {
        var cmd=new SqlCommand(sql,c,t){CommandTimeout=AppConfig.CommandTimeoutSeconds};
        cmd.Parameters.Add("@No",SqlDbType.NVarChar,120).Value=no;cmd.Parameters.Add("@Category",SqlDbType.NVarChar,40).Value=category;
        cmd.Parameters.Add("@Version",SqlDbType.Int).Value=version;return cmd;
    }
    internal static DataTable Load(SqlConnection c,SqlTransaction t,string no,string category,int version)
    { using var cmd=Command(c,t,RowsSql,no,category,version);using var r=cmd.ExecuteReader();var table=new DataTable();table.Load(r);return table; }
    internal static string Json(SqlConnection c,SqlTransaction t,string no,string category,int version)
    { using var cmd=Command(c,t,"SELECT ("+RowsSql+" FOR JSON PATH,INCLUDE_NULL_VALUES);",no,category,version);return Convert.ToString(cmd.ExecuteScalar(),CultureInfo.InvariantCulture) ?? "[]"; }
    internal static bool HasControlledCreation(SqlConnection c,SqlTransaction t,string no,string category,int version)
    { using var cmd=Command(c,t,@"SELECT COUNT(*) FROM dbo.PRM_SpecificationContentHistory WITH(UPDLOCK,HOLDLOCK)
WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version AND ActionType=N'Draft Created';",no,category,version);
        return Convert.ToInt32(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)>0; }
    internal static void EnsureReviewIndependent(SqlConnection c,SqlTransaction t,string no,string category,int version,string signer)
    {
        using var cmd=Command(c,t,@"IF EXISTS(SELECT 1 FROM dbo.PRM_SpecificationTests WITH(UPDLOCK,HOLDLOCK)
WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version AND UPPER(LTRIM(RTRIM(CreatedBy)))=UPPER(LTRIM(RTRIM(@Signer))))
OR EXISTS(SELECT 1 FROM dbo.PRM_SpecificationContentHistory WITH(UPDLOCK,HOLDLOCK)
WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version AND ActionType IN(N'Draft Created',N'Draft Updated') AND UPPER(LTRIM(RTRIM(ChangedBy)))=UPPER(LTRIM(RTRIM(@Signer))))
THROW 56462,'The specification reviewer must be independent from every draft author/editor.',1;",no,category,version);
        cmd.Parameters.Add("@Signer",SqlDbType.NVarChar,120).Value=signer;cmd.ExecuteNonQuery();
    }
    internal static void Record(SqlConnection c,SqlTransaction t,string no,string category,int version,string action,string before,string editor,string reason,long? signatureId=null)
    {
        string after=Json(c,t,no,category,version);
        if(string.IsNullOrWhiteSpace(editor) || string.IsNullOrWhiteSpace(reason) || reason.Length>1000) throw new InvalidOperationException("Content history requires an editor and controlled reason.");
        using var cmd=Command(c,t,@"INSERT dbo.PRM_SpecificationContentHistory
(SpecificationNo,SampleCategory,VersionNo,ActionType,OldRowsJson,NewRowsJson,ChangedBy,ChangeReason,SignatureID,ContentHash)
VALUES(@No,@Category,@Version,@Action,@Before,@After,@User,@Reason,@Signature,@Hash);",no,category,version);
        cmd.Parameters.Add("@Action",SqlDbType.NVarChar,60).Value=action;cmd.Parameters.Add("@Before",SqlDbType.NVarChar,-1).Value=before;
        cmd.Parameters.Add("@After",SqlDbType.NVarChar,-1).Value=after;cmd.Parameters.Add("@User",SqlDbType.NVarChar,120).Value=editor;
        cmd.Parameters.Add("@Reason",SqlDbType.NVarChar,1000).Value=reason;cmd.Parameters.Add("@Signature",SqlDbType.BigInt).Value=signatureId.HasValue ? signatureId.Value : DBNull.Value;
        cmd.Parameters.Add("@Hash",SqlDbType.Binary,32).Value=SHA256.HashData(Encoding.UTF8.GetBytes(after));cmd.ExecuteNonQuery();
    }
}
