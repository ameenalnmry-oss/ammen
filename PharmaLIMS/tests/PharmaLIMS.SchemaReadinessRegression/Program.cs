using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;

if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Native disposable LocalDB regression requires Windows.");
// No inherited SQL target, credential, Production database or retained site UAT is used.
var master=new SqlConnectionStringBuilder {DataSource=@"(localdb)\MSSQLLocalDB",InitialCatalog="master",IntegratedSecurity=true,Encrypt=SqlConnectionEncryptOption.Optional,TrustServerCertificate=false};
string name="PharmaLIMS_Integration_Schema_"+Guid.NewGuid().ToString("N")[..12];
var target=new SqlConnectionStringBuilder(master.ConnectionString){InitialCatalog=name};
async Task Exec(string cs,string sql){using var c=new SqlConnection(cs);await c.OpenAsync();using var q=new SqlCommand(sql,c);await q.ExecuteNonQueryAsync();}
bool created=false;
try
{
    await Exec(master.ConnectionString,$"CREATE DATABASE [{name}]");created=true;
    await Exec(target.ConnectionString,@"
CREATE TABLE dbo.CultureMedia(MediaID int PRIMARY KEY);
CREATE TABLE dbo.CultureMediaLots(MediaLotID int PRIMARY KEY);
CREATE TABLE dbo.MediaPreparations(WorkflowRowVersion rowversion);
CREATE TABLE dbo.WaterResultResourceEvidence(ExecutionDate date);
CREATE TABLE dbo.PRM_SpecificationSignatures(SignatureID int PRIMARY KEY);
CREATE TABLE dbo.PRM_SpecificationContentHistory(SignatureID int NULL REFERENCES dbo.PRM_SpecificationSignatures(SignatureID),ContentHash binary(32) NOT NULL);
CREATE UNIQUE INDEX UX_PRM_SpecificationContentHistory_Signature ON dbo.PRM_SpecificationContentHistory(SignatureID) WHERE SignatureID IS NOT NULL;");
    foreach(var rule in new[]{("CultureMedia","TRG_CultureMedia_ReferencedIdentity","Referenced culture media identity is frozen"),("CultureMediaLots","TRG_CultureMediaLots_ReferencedIdentity","Qualified/prepared culture media lot identity is frozen"),("PRM_SpecificationContentHistory","TRG_PRM_SpecificationContentHistory_AppendOnly","PRM specification content history is append-only and cannot be updated or deleted.")})
        await Exec(target.ConnectionString,$"CREATE TRIGGER dbo.{rule.Item2} ON dbo.{rule.Item1} AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; THROW 56456,'{rule.Item3}',1; END;");
    using var connection=new SqlConnection(target.ConnectionString);await connection.OpenAsync();
    using(var baseline=new SqlCommand(ReviewClosureSchemaContract.Sql,connection))using(var reader=await baseline.ExecuteReaderAsync())
        if(await reader.ReadAsync())throw new Exception("Healthy schema fixture must pass the actual readiness contract.");
    await AppendOnlyPreflightCases.RunAsync(connection);
}
finally
{
    SqlConnection.ClearAllPools();
    if(created)await Exec(master.ConnectionString,$"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]");
}
