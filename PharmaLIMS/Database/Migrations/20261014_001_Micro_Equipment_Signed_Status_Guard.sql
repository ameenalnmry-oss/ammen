SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.MicroEquipmentActivitySignatures',N'U') IS NULL OR OBJECT_ID(N'dbo.MicroEquipmentActivities',N'U') IS NULL
 THROW 56520,'Micro equipment ledger prerequisite is missing.',1;
IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentApp') IS NULL CREATE ROLE PharmaLIMS_EquipmentApp;
-- The actual application login must be assigned this restricted role during controlled deployment.
DENY INSERT ON dbo.MicroEquipmentActivitySignatures TO PharmaLIMS_EquipmentApp;
-- Other activity fields retain existing write permissions; the trigger protects signed status transitions.
EXEC(N'CREATE OR ALTER PROCEDURE dbo.MicroEquipment_SignActivityStage
 @Activity bigint,@Version binary(8),@Actor nvarchar(100),@Stage nvarchar(30),@Reason nvarchar(500)
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON;
 IF @Stage NOT IN(N''Reviewed'',N''Approved'') OR NULLIF(LTRIM(RTRIM(@Reason)),N'''') IS NULL THROW 56515,''Invalid signed stage or reason.'',1;

DECLARE @Prior nvarchar(30)=CASE WHEN @Stage=N''Reviewed'' THEN N''Submitted'' ELSE N''Reviewed'' END;
IF NOT EXISTS(
 SELECT 1 FROM dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK)
 WHERE ActivityID=@Activity AND ActivityStatus=@Prior AND VersionToken=@Version
 AND PerformedBy<>@Actor AND ActualStartAt IS NOT NULL AND ActualEndAt IS NOT NULL
 AND NULLIF(LTRIM(RTRIM(MethodReference)),N'''') IS NOT NULL
 AND (ActivityType<>N''Calibration'' OR
      (EXISTS (SELECT 1 FROM dbo.MicroEquipmentStructuredEvidence se
               WHERE se.ActivityID=@Activity AND se.EvidenceType=N''Calibration'')
       AND EXISTS (SELECT 1 FROM dbo.MicroEquipmentCalibrationPoints cp WHERE cp.ActivityID=@Activity)
       AND EXISTS (SELECT 1 FROM dbo.MicroEquipmentCalibrationPoints p
                   JOIN dbo.MicroEquipmentStructuredEvidence s ON s.ActivityID=p.ActivityID
                   WHERE p.ActivityID=@Activity AND p.PointNo=1
                   AND p.ObservedValue=s.ObservedValue
                   AND ((p.NormalMinimum=s.NormalMinimum AND p.NormalMaximum=s.NormalMaximum
                         AND p.ReferenceValue IS NULL AND s.ReferenceValue IS NULL)
                     OR (p.ReferenceValue=s.ReferenceValue AND p.MaxAbsoluteError=s.MaximumAbsoluteError
                         AND p.NormalMinimum IS NULL AND s.NormalMinimum IS NULL)))
       AND NOT EXISTS (SELECT 1 FROM dbo.MicroEquipmentCalibrationPoints cp
                       WHERE cp.ActivityID=@Activity AND (cp.Outcome IS NULL OR
                        (cp.NormalMinimum IS NULL AND EXISTS
                         (SELECT 1 FROM dbo.MicroEquipmentStructuredEvidence se WHERE se.ActivityID=@Activity AND se.NormalMinimum IS NOT NULL)) OR
                        (cp.NormalMinimum IS NOT NULL AND EXISTS
                         (SELECT 1 FROM dbo.MicroEquipmentStructuredEvidence se WHERE se.ActivityID=@Activity AND se.NormalMinimum IS NULL)))))
 AND (@Stage<>N''Approved'' OR ActivityType NOT IN (N''Calibration'',N''Cleaning'',N''Sanitization'')
      OR (EXISTS
          (SELECT 1 FROM dbo.MicroEquipmentStructuredEvidence se
           WHERE se.ActivityID=@Activity AND se.EvidenceType=ActivityType AND se.Outcome=N''PASS'')
          AND (ActivityType<>N''Calibration'' OR NOT EXISTS
              (SELECT 1 FROM dbo.MicroEquipmentCalibrationPoints cp
               WHERE cp.ActivityID=@Activity AND cp.Outcome<>N''PASS''))))
 AND (@Stage<>N''Approved'' OR EXISTS(
 SELECT 1 FROM dbo.MicroEquipmentActivitySignatures
 WHERE ActivityID=@Activity AND SignatureMeaning=N''Reviewed'' AND SignedBy<>@Actor))
)) THROW 56513,''Activity stage is stale or segregation/record requirements are unmet.'',1;

DECLARE @Snapshot nvarchar(max)=
 (SELECT ActivityID,EquipmentID,ActivityType,@Stage AS ActivityStatus,PerformedBy,
         ActualStartAt,ActualEndAt,MethodReference,ActivityReason,
         EquipmentCodeSnapshot,EquipmentNameSnapshot,CalibrationStatusSnapshot,QualificationStatusSnapshot,
         JSON_QUERY((SELECT EvidenceType,ReferenceValue,ObservedValue,MaximumAbsoluteError,
                            AbsoluteError,NormalMinimum,NormalMaximum,CleaningAgentLot,Outcome,RecordedBy,RecordedAt
                     FROM dbo.MicroEquipmentStructuredEvidence
                     WHERE ActivityID=@Activity FOR JSON PATH,INCLUDE_NULL_VALUES)) AS StructuredEvidence,
         JSON_QUERY((SELECT PointNo,ReferenceValue,ObservedValue,MaxAbsoluteError,NormalMinimum,NormalMaximum,AbsoluteError,Outcome
                     FROM dbo.MicroEquipmentCalibrationPoints WHERE ActivityID=@Activity
                     ORDER BY PointNo FOR JSON PATH,INCLUDE_NULL_VALUES)) AS CalibrationPoints
  FROM dbo.MicroEquipmentActivities WHERE ActivityID=@Activity
  FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
IF @Snapshot IS NULL THROW 56514,''Signed equipment record is missing.'',1;
INSERT dbo.MicroEquipmentActivitySignatures
 (ActivityID,SignatureMeaning,SignatureReason,ContentHash,SignedBy)
 VALUES(@Activity,@Stage,@Reason,HASHBYTES(''SHA2_256'',CONVERT(varbinary(max),@Snapshot)),@Actor);
UPDATE dbo.MicroEquipmentActivities SET ActivityStatus=@Stage WHERE ActivityID=@Activity;
END;');
GRANT EXECUTE ON dbo.MicroEquipment_SignActivityStage TO PharmaLIMS_EquipmentApp;
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_MicroEquipmentActivities_SignedStatus ON dbo.MicroEquipmentActivities AFTER UPDATE AS
BEGIN
 SET NOCOUNT ON;
 IF NOT UPDATE(ActivityStatus) RETURN;
 IF EXISTS(
  SELECT 1 FROM inserted i JOIN deleted d ON i.ActivityID=d.ActivityID
  WHERE i.ActivityStatus<>d.ActivityStatus
    AND i.ActivityStatus IN(N''Reviewed'',N''Approved'')
    AND (NOT ((d.ActivityStatus=N''Submitted'' AND i.ActivityStatus=N''Reviewed'') OR
              (d.ActivityStatus=N''Reviewed'' AND i.ActivityStatus=N''Approved''))
       OR NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivitySignatures s
             WHERE s.ActivityID=i.ActivityID AND s.SignatureMeaning=i.ActivityStatus
               AND NULLIF(LTRIM(RTRIM(s.SignedBy)),N'''') IS NOT NULL
               AND NULLIF(LTRIM(RTRIM(s.SignatureReason)),N'''') IS NOT NULL
               AND s.ContentHash IS NOT NULL)))
 THROW 56521,''Signed micro equipment status transition requires prior controlled signature.'',1;
END;');
