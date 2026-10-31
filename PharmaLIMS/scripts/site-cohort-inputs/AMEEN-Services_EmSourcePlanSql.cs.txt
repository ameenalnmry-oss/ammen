namespace PharmaLIMS.Services;
internal static class EmSourcePlanSql
{
    internal const string Cancel = @"SET NOCOUNT ON;
IF NOT EXISTS(SELECT 1 FROM dbo.EM_Plans WITH(UPDLOCK,HOLDLOCK) WHERE PlanID=@Plan AND Status=@Expected AND Status NOT IN(N'Completed',N'Cancelled'))
THROW 55515,'The EM plan changed or can no longer be cancelled.',1;
IF EXISTS(SELECT 1 FROM dbo.EM_Events E WITH(UPDLOCK,HOLDLOCK) WHERE E.PlanID=@Plan AND
(COALESCE(NULLIF(LTRIM(RTRIM(E.WorkflowStatus)),N''),LTRIM(RTRIM(E.FinalResult))) IN(N'Approved',N'Completed') OR E.ApprovedBy IS NOT NULL OR E.ApprovedDate IS NOT NULL OR EXISTS(SELECT 1 FROM dbo.EM_EventSignatures s WHERE s.EventID=E.Id AND s.ActionType=N'EM Approval')))
THROW 55516,'A plan with approved EM evidence cannot be cancelled.',1;
DECLARE @Changes TABLE(TableName nvarchar(80),RecordID int,RecordNo nvarchar(100),OldStatus nvarchar(100));
UPDATE dbo.EM_Plans SET Status=N'Cancelled',CancelledBy=@User,CancelledAt=SYSDATETIME(),CancellationReason=@Reason
OUTPUT N'EM_Plans',inserted.PlanID,inserted.PlanNo,deleted.Status INTO @Changes WHERE PlanID=@Plan AND Status=@Expected;
UPDATE dbo.EM_Events SET WorkflowStatus=N'Cancelled' OUTPUT N'EM_Events',inserted.Id,inserted.EventNo,deleted.WorkflowStatus INTO @Changes WHERE PlanID=@Plan;
UPDATE dbo.EM_PlanSamples SET Status=N'Cancelled' WHERE PlanID=@Plan AND Status<>N'Completed';
INSERT dbo.EM_EventSignatures(EventID,EventNo,ActionType,ActionReason,SignedBy,UserRole,MeaningOfSignature,SignedAt)
SELECT RecordID,RecordNo,N'EM Plan Cancellation',@Reason,@User,@Role,@Meaning,SYSDATETIME() FROM @Changes WHERE TableName=N'EM_Events';
INSERT dbo.EM_PlanSignatures(PlanID,ActionType,SignedBy,UserRole,MeaningOfSignature,ActionReason,SignedAt)
VALUES(@Plan,N'Cancelled',@User,@Role,@Meaning,@Reason,SYSDATETIME());
SELECT TableName,RecordID,RecordNo,OldStatus FROM @Changes;";
}
