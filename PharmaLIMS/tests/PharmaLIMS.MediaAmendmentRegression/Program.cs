using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;

string root = args[0];
using var config = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("EQ001_SQL_TEST_SETTINGS") ?? throw new Exception("Explicit disposable SQL settings required")));
var builder = config.RootElement.TryGetProperty("MasterConnectionString",out var m) ? new SqlConnectionStringBuilder(m.GetString()) : new SqlConnectionStringBuilder { DataSource = config.RootElement.GetProperty("server").GetString(), InitialCatalog="master", UserID="sa", Password=config.RootElement.GetProperty("password").GetString(), Encrypt=SqlConnectionEncryptOption.Mandatory,TrustServerCertificate=false,Pooling=false };
string db = "MediaAmendment_" + Guid.NewGuid().ToString("N");
using var master = new SqlConnection(builder.ConnectionString); master.Open();
void Execute(SqlConnection c, string sql, SqlTransaction? tx = null) { using var cmd = new SqlCommand(sql,c,tx); cmd.ExecuteNonQuery(); }
void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
void Reject(Action action, string message) { try { action(); } catch (DBConcurrencyException) { return; } throw new Exception("FAIL: " + message); }
Execute(master, $"CREATE DATABASE [{db}]");
try
{
 builder.InitialCatalog=db;
 using var c = new SqlConnection(builder.ConnectionString); c.Open();
 Execute(c,@"
CREATE TABLE dbo.MediaPreparations(MediaPreparationID int PRIMARY KEY,MediaLotID int,PowderQuantityG decimal(18,3),FinalPH nvarchar(30),ReleaseStatus nvarchar(30),VisualCheckedBy nvarchar(100),VisualCheckedAt datetime2,SterilityReviewedBy nvarchar(100),SterilityReviewedAt datetime2,ReleasedBy nvarchar(100),ReleasedAt datetime2);
CREATE TABLE dbo.CultureMediaSopFields(ID int IDENTITY PRIMARY KEY,EntityType nvarchar(50),EntityID int,AnnexureCode nvarchar(100),FieldName nvarchar(100),FieldValue nvarchar(max));
CREATE TABLE dbo.CultureMediaSignatures(ID int IDENTITY PRIMARY KEY,EntityType nvarchar(50),EntityID int,ActionType nvarchar(100),SignedBy nvarchar(100));
CREATE TABLE dbo.TestAudit(OldValue nvarchar(max),NewValue nvarchar(max));
INSERT dbo.MediaPreparations VALUES(1,10,10,N'7.1',N'Under Release',NULL,NULL,NULL,NULL,NULL,NULL);
INSERT dbo.CultureMediaSopFields VALUES(N'MediaPreparation',1,N'1035-L-0005/A6',N'LoadNo',N'LOAD-OLD');
INSERT dbo.CultureMediaSignatures VALUES(N'MediaPreparation',1,N'Prepare Culture Media',N'preparer');");
 string source=File.ReadAllText(Path.Combine(root,"CultureMediaPreparation.xaml.Part2.cs"));
 string query=Regex.Match(source,"using \\(var lockCommand = new SqlCommand\\(@\"(.*?)\", conn, tx\\)\\)",RegexOptions.Singleline).Groups[1].Value;
 Check(!string.IsNullOrWhiteSpace(query), "application amendment lock query not found");
 using (var tx=c.BeginTransaction())
 {
  Execute(c,"UPDATE dbo.MediaPreparations SET VisualCheckedBy=N'reviewer',VisualCheckedAt=SYSUTCDATETIME()",tx);
  using var cmd=new SqlCommand(query,c,tx); cmd.Parameters.AddWithValue("@MediaPreparationID",1);
  using var reader=cmd.ExecuteReader();
  Check(!reader.Read(),"an actor signed visual review during reauthentication, but actual amendment SQL still admits the signed scientific record");
  reader.Close(); tx.Rollback();
 }
 Console.WriteLine("PASS actual amendment SQL rejects concurrent signed review (baseline RED / fixed GREEN)");
 MediaPreparationAmendmentSnapshot Read(SqlTransaction tx) => MediaPreparationAmendmentSnapshot.Read(c,tx,1,10);
 using (var tx=c.BeginTransaction())
 {
  var loaded=Read(tx); MediaPreparationAmendmentSnapshot.EnsureCanAmend(loaded,Read(tx));
  Check(loaded.SopField("1035-L-0005/A6","LoadNo")=="LOAD-OLD","displayed SOP not from exact snapshot");
  Reject(()=>MediaPreparationAmendmentSnapshot.EnsureCanAmend(null,loaded),"missing loaded evidence accepted");
  tx.Rollback();
 }
 Console.WriteLine("PASS unsigned preparation amendable; missing loaded evidence fails closed");
 var changes = new Dictionary<string,string> {
  ["scientific pH change"]="UPDATE dbo.MediaPreparations SET FinalPH=N'7.2'",
  ["concurrent powder amendment"]="UPDATE dbo.MediaPreparations SET PowderQuantityG=12",
  ["SOP scientific change"]="UPDATE dbo.CultureMediaSopFields SET FieldValue=N'LOAD-NEW'",
  ["SOP insert"]="INSERT dbo.CultureMediaSopFields VALUES(N'MediaPreparation',1,N'1035-L-0005/A6',N'Additives',N'new')",
  ["SOP delete"]="DELETE dbo.CultureMediaSopFields",
  ["review signature insert"]="INSERT dbo.CultureMediaSignatures VALUES(N'MediaPreparation',1,N'Visual Check Prepared Media',N'reviewer')",
  ["signature change"]="UPDATE dbo.CultureMediaSignatures SET SignedBy=N'other'",
  ["visual review columns"]="UPDATE dbo.MediaPreparations SET VisualCheckedBy=N'reviewer',VisualCheckedAt=SYSUTCDATETIME()",
  ["sterility review columns"]="UPDATE dbo.MediaPreparations SET SterilityReviewedBy=N'reviewer',SterilityReviewedAt=SYSUTCDATETIME()",
  ["final release"]="UPDATE dbo.MediaPreparations SET ReleaseStatus=N'Released',ReleasedBy=N'qa',ReleasedAt=SYSUTCDATETIME()"
 };
 foreach (var change in changes)
 {
  using var tx=c.BeginTransaction(); var loaded=Read(tx); Execute(c,change.Value,tx); var current=Read(tx);
  Reject(()=>MediaPreparationAmendmentSnapshot.EnsureCanAmend(loaded,current),change.Key+" accepted by amendment");
  Reject(()=>MediaPreparationAmendmentSnapshot.EnsureUnchanged(loaded,current),change.Key+" accepted by review/release");
  tx.Rollback(); Console.WriteLine("PASS stale amendment/review/release rejects " + change.Key);
 }
 foreach (string sql in new[] {
  "UPDATE dbo.MediaPreparations SET VisualCheckedBy=N'reviewer'",
  "UPDATE dbo.MediaPreparations SET SterilityReviewedAt=SYSUTCDATETIME()",
  "UPDATE dbo.MediaPreparations SET ReleasedBy=N'qa'",
  "INSERT dbo.CultureMediaSignatures VALUES(N'MediaPreparation',1,N'Visual Check Prepared Media',N'reviewer')",
  "INSERT dbo.CultureMediaSignatures VALUES(N'MediaPreparation',1,N'Sterility Review',N'reviewer')",
  "INSERT dbo.CultureMediaSignatures VALUES(N'MediaPreparation',1,N'Final Release Prepared Media',N'qa')" })
 {
  using var tx=c.BeginTransaction(); Execute(c,sql,tx); var signed=Read(tx);
  Reject(()=>MediaPreparationAmendmentSnapshot.EnsureCanAmend(signed,signed),"already signed record accepted"); tx.Rollback();
 }
 Console.WriteLine("PASS freshly loaded signed records immutable (six review/release forms)");
 using (var tx=c.BeginTransaction())
 {
  var old=Read(tx);
  Execute(c,"UPDATE dbo.MediaPreparations SET FinalPH=N'7.3',PowderQuantityG=11; UPDATE dbo.CultureMediaSopFields SET FieldValue=N'LOAD-AMENDED'; INSERT dbo.CultureMediaSignatures VALUES(N'MediaPreparation',1,N'Amend Culture Media Preparation',N'amender')",tx);
  var updated=Read(tx);
  using var audit=new SqlCommand("INSERT dbo.TestAudit VALUES(@Old,@New)",c,tx);
  audit.Parameters.AddWithValue("@Old",old.AuditValue); audit.Parameters.AddWithValue("@New",updated.AuditValue); audit.ExecuteNonQuery();
  using var readAudit=new SqlCommand("SELECT OldValue,NewValue FROM dbo.TestAudit",c,tx); using var reader=readAudit.ExecuteReader(); reader.Read();
  using var oldJson=JsonDocument.Parse(reader.GetString(0)); using var newJson=JsonDocument.Parse(reader.GetString(1));
  Check(oldJson.RootElement.GetProperty("Preparation")[0].GetProperty("FinalPH").GetString()=="7.1","old pH audit absent");
  Check(newJson.RootElement.GetProperty("Preparation")[0].GetProperty("FinalPH").GetString()=="7.3","new pH audit absent");
  Check(oldJson.RootElement.GetProperty("Preparation")[0].GetProperty("PowderQuantityG").GetDecimal()==10m,"old powder audit absent");
  Check(newJson.RootElement.GetProperty("Preparation")[0].GetProperty("PowderQuantityG").GetDecimal()==11m,"new powder audit absent");
  Check(oldJson.RootElement.GetProperty("SopFields")[0].GetProperty("FieldValue").GetString()=="LOAD-OLD","old SOP audit absent");
  Check(newJson.RootElement.GetProperty("SopFields")[0].GetProperty("FieldValue").GetString()=="LOAD-AMENDED","new SOP audit absent");
  Check(newJson.RootElement.GetProperty("Signatures").GetArrayLength()==2,"signature history lost");
  reader.Close(); tx.Rollback();
 }
 Console.WriteLine("PASS SQL-persisted old/new audit retains real pH, powder, SOP and complete signature history");
 using (var tx=c.BeginTransaction())
 {
  _=Read(tx);
  foreach(string sql in new[] {
   "UPDATE dbo.MediaPreparations SET FinalPH=N'8' WHERE MediaPreparationID=1",
   "UPDATE dbo.CultureMediaSopFields SET FieldValue=N'raced' WHERE EntityType=N'MediaPreparation' AND EntityID=1",
   "INSERT dbo.CultureMediaSignatures VALUES(N'MediaPreparation',1,N'Visual Check Prepared Media',N'racer')" })
  {
   using var competing=new SqlConnection(builder.ConnectionString); competing.Open();
   try { Execute(competing,"SET LOCK_TIMEOUT 300; "+sql); throw new Exception("FAIL: evidence lock allowed competing writer"); }
   catch(SqlException ex) when(ex.Number==1222) { }
  }
  tx.Rollback();
 }
 Console.WriteLine("PASS competing connections blocked for header/SOP/review signatures until transaction ends");
 int start=source.IndexOf("private void BtnSavePreparation_Click",StringComparison.Ordinal);
 int end=source.IndexOf("private void BtnSignPreparationVisualCheck_Click",start,StringComparison.Ordinal);
 string amendment=source[start..end];
 int guard=amendment.IndexOf("EnsureCanAmend(_loadedPreparationSnapshot, originalSnapshot)",StringComparison.Ordinal);
 Check(guard>=0 && guard<amendment.IndexOf("UPDATE dbo.MediaPreparations",StringComparison.Ordinal),"guard not before production writes");
 Check(amendment.Contains("originalSnapshot?.AuditValue ?? string.Empty, savedSnapshot.AuditValue"),"audit not wired to real old/new evidence");
 Check(amendment.Contains("originalPowderQuantityG, powderQuantityG"),"delta stock path lost");
 Check(amendment.Contains("if (originalLotId != mediaLotId)"),"immutable source lot lost");
 Console.WriteLine("PASS handler wires guard and full audits while preserving stock delta/source lot");
}
finally { Execute(master,$"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}];"); }
