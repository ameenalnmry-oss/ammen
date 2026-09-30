using System.Data;
using Microsoft.Data.SqlClient;

namespace PharmaLIMS.Services;

internal static class LaboratoryReceiptSql
{
    // Source locks serialize receipt capture with review/approval and concurrent capture.
    internal const string Insert = @"
DECLARE @SourceNumber nvarchar(100), @sampled datetime2, @status nvarchar(100);
IF @Kind=N'PRM'
 SELECT @SourceNumber=SampleNumber,@sampled=SampleDateTime,@status=SampleStatus
 FROM dbo.PRM_Samples WITH(UPDLOCK,HOLDLOCK) WHERE SampleID=@Id;
ELSE IF @Kind=N'EM'
 SELECT @SourceNumber=EventNo,@sampled=EventDate,@status=ISNULL(NULLIF(WorkflowStatus,N''),N'Registered')
 FROM dbo.EM_Events WITH(UPDLOCK,HOLDLOCK) WHERE Id=@Id;
ELSE THROW 55302,'Select a PRM sample or EM event. Water receipt is recorded during water registration.',1;
IF @SourceNumber IS NULL OR @SourceNumber<>@Number THROW 55303,'Source identity changed. Reload the register.',1;
IF UPPER(LTRIM(RTRIM(ISNULL(@status,N'')))) NOT IN (N'REGISTERED',N'PENDING')
 THROW 55304,'Receipt capture is available only for registered or pending records, before results/review.',1;
IF (@Kind=N'PRM' AND EXISTS(SELECT 1 FROM dbo.PRM_SampleTests WITH(UPDLOCK,HOLDLOCK)
 WHERE SampleID=@Id AND (NULLIF(LTRIM(RTRIM(ResultValue)),N'') IS NOT NULL OR EnteredDate IS NOT NULL)))
 OR (@Kind=N'EM' AND EXISTS(SELECT 1 FROM dbo.EM_EventPlates WITH(UPDLOCK,HOLDLOCK)
 WHERE EventId=@Id AND (TotalCount IS NOT NULL OR FungalCount IS NOT NULL OR ResultCFU IS NOT NULL)))
 THROW 55307,'Results already exist. Receipt capture requires controlled reconciliation.',1;
IF @sampled IS NULL OR @Received<@sampled OR @Received>SYSDATETIME()
 THROW 55305,'Receipt must be on or after sampling and cannot be in the future (server time).',1;
IF EXISTS(SELECT 1 FROM dbo.LaboratoryReceipts WITH(UPDLOCK,HOLDLOCK)
 WHERE (@Kind=N'PRM' AND PrmSampleID=@Id) OR (@Kind=N'EM' AND EmEventID=@Id))
 THROW 55306,'A signed laboratory receipt already exists. It cannot be overwritten.',1;
INSERT dbo.LaboratoryReceipts(PrmSampleID,EmEventID,SampleNumber,ReceivedDateTime,ReceivedBy,
 ReceiptDecision,ActionReason,MeaningOfSignature,UserRole,SourceWorkstation)
OUTPUT INSERTED.ReceiptID
VALUES(CASE WHEN @Kind=N'PRM' THEN @Id END,CASE WHEN @Kind=N'EM' THEN @Id END,
 @Number,@Received,@Signer,N'Accepted',@Reason,@Meaning,@Role,@Workstation);";

    internal static SqlParameter[] Parameters(string kind,int id,string number,DateTime received,
        string signer,string reason,string meaning,string role,string workstation)
    {
        if (kind is not ("PRM" or "EM") || id<=0) throw new InvalidOperationException("Select a PRM sample or EM event.");
        if (string.IsNullOrWhiteSpace(signer) || reason.Trim().Length<10 || reason.Length>1000 ||
            meaning.Trim().Length<3 || meaning.Length>255 || number.Length>100 || signer.Length>100 ||
            string.IsNullOrWhiteSpace(role) || role.Length>100 || workstation.Length>200)
            throw new InvalidOperationException("A valid signer, signature meaning and specific reason (10–1000 characters) are required.");
        return new[] {
            new SqlParameter("@Kind",SqlDbType.NVarChar,3){Value=kind},
            new SqlParameter("@Id",SqlDbType.Int){Value=id},
            new SqlParameter("@Number",SqlDbType.NVarChar,100){Value=number},
            new SqlParameter("@Received",SqlDbType.DateTime2){Value=received},
            new SqlParameter("@Signer",SqlDbType.NVarChar,100){Value=signer},
            new SqlParameter("@Reason",SqlDbType.NVarChar,1000){Value=reason.Trim()},
            new SqlParameter("@Meaning",SqlDbType.NVarChar,255){Value=meaning.Trim()},
            new SqlParameter("@Role",SqlDbType.NVarChar,100){Value=role},
            new SqlParameter("@Workstation",SqlDbType.NVarChar,200){Value=workstation}
        };
    }
}
