SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.MicroEquipmentActivities',N'U') IS NULL
 THROW 56601,'Micro equipment activity ledger must be installed first.',1;
IF OBJECT_ID(N'dbo.MicroEquipmentStructuredEvidence',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.MicroEquipmentStructuredEvidence
 (
  ActivityID BIGINT NOT NULL CONSTRAINT PK_MicroEquipmentStructuredEvidence PRIMARY KEY,
  EvidenceType NVARCHAR(30) NOT NULL,
  ReferenceValue DECIMAL(18,6) NULL,
  ObservedValue DECIMAL(18,6) NULL,
  MaximumAbsoluteError DECIMAL(18,6) NULL,
  AbsoluteError AS ABS(ObservedValue-ReferenceValue) PERSISTED,
  NormalMinimum DECIMAL(18,6) NULL,
  NormalMaximum DECIMAL(18,6) NULL,
  CleaningAgentLot NVARCHAR(90) NULL,
  Outcome NVARCHAR(10) NOT NULL,
  RecordedBy NVARCHAR(100) NOT NULL,
  RecordedAt DATETIME2(0) NOT NULL CONSTRAINT DF_MicroEquipmentStructuredEvidence_RecordedAt DEFAULT SYSUTCDATETIME(),
  CONSTRAINT FK_MicroEquipmentStructuredEvidence_Activity FOREIGN KEY(ActivityID) REFERENCES dbo.MicroEquipmentActivities(ActivityID),
  CONSTRAINT CK_MicroEquipmentStructuredEvidence_Type CHECK(EvidenceType IN(N'Calibration',N'Cleaning',N'Sanitization')),
  CONSTRAINT CK_MicroEquipmentStructuredEvidence_Outcome CHECK(Outcome IN(N'PASS',N'FAIL')),
  CONSTRAINT CK_MicroEquipmentStructuredEvidence_Values CHECK
   ((EvidenceType=N'Calibration' AND ObservedValue IS NOT NULL AND CleaningAgentLot IS NULL AND
      ((ReferenceValue IS NOT NULL AND MaximumAbsoluteError IS NOT NULL AND MaximumAbsoluteError>=0
        AND NormalMinimum IS NULL AND NormalMaximum IS NULL
        AND ((ABS(ObservedValue-ReferenceValue)<=MaximumAbsoluteError AND Outcome=N'PASS') OR
             (ABS(ObservedValue-ReferenceValue)>MaximumAbsoluteError AND Outcome=N'FAIL')))
       OR (ReferenceValue IS NULL AND MaximumAbsoluteError IS NULL AND NormalMinimum IS NOT NULL
        AND NormalMaximum IS NOT NULL AND NormalMinimum<=NormalMaximum
        AND ((ObservedValue BETWEEN NormalMinimum AND NormalMaximum AND Outcome=N'PASS') OR
             ((ObservedValue<NormalMinimum OR ObservedValue>NormalMaximum) AND Outcome=N'FAIL')))))
    OR (EvidenceType IN(N'Cleaning',N'Sanitization') AND CleaningAgentLot IS NOT NULL
      AND LEN(LTRIM(RTRIM(CleaningAgentLot)))>0 AND ReferenceValue IS NULL AND ObservedValue IS NULL
      AND MaximumAbsoluteError IS NULL AND NormalMinimum IS NULL AND NormalMaximum IS NULL))
 );
END;
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_MicroEquipmentStructuredEvidence_AppendOnly ON dbo.MicroEquipmentStructuredEvidence AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 56602, ''Signed equipment execution evidence cannot be updated or deleted.'',1; END;');
IF OBJECT_ID(N'dbo.MicroEquipmentCalibrationPoints',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.MicroEquipmentCalibrationPoints(
  ActivityID BIGINT NOT NULL,
  PointNo INT NOT NULL,
  ReferenceValue DECIMAL(18,6) NULL,
  ObservedValue DECIMAL(18,6) NOT NULL,
  MaxAbsoluteError DECIMAL(18,6) NULL,
  NormalMinimum DECIMAL(18,6) NULL,
  NormalMaximum DECIMAL(18,6) NULL,
  AbsoluteError AS ABS(ObservedValue-ReferenceValue) PERSISTED,
  Outcome AS (CASE WHEN NormalMinimum IS NOT NULL THEN CASE WHEN ObservedValue BETWEEN NormalMinimum AND NormalMaximum THEN N'PASS' ELSE N'FAIL' END WHEN ABS(ObservedValue-ReferenceValue)<=MaxAbsoluteError THEN N'PASS' ELSE N'FAIL' END) PERSISTED,
  CONSTRAINT PK_MicroEquipmentCalibrationPoints PRIMARY KEY(ActivityID,PointNo),
  CONSTRAINT FK_MicroEquipmentCalibrationPoints_Activity FOREIGN KEY(ActivityID) REFERENCES dbo.MicroEquipmentActivities(ActivityID),
  CONSTRAINT CK_MicroEquipmentCalibrationPoints_Positive CHECK(PointNo BETWEEN 1 AND 100 AND ((ReferenceValue IS NOT NULL AND MaxAbsoluteError IS NOT NULL AND MaxAbsoluteError>=0 AND NormalMinimum IS NULL AND NormalMaximum IS NULL) OR (ReferenceValue IS NULL AND MaxAbsoluteError IS NULL AND NormalMinimum IS NOT NULL AND NormalMaximum IS NOT NULL AND NormalMinimum<=NormalMaximum)))
 );
END;
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_MicroEquipmentCalibrationPoints_AppendOnly ON dbo.MicroEquipmentCalibrationPoints AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 56604, ''Calibration point evidence is append-only.'',1; END;');
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_MicroEquipmentCalibrationPoints_Identity ON dbo.MicroEquipmentCalibrationPoints AFTER INSERT AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.MicroEquipmentActivities a ON a.ActivityID=i.ActivityID WHERE a.ActivityType<>N''Calibration'' OR a.ActivityStatus<>N''Submitted'') THROW 56605, ''Calibration points require a submitted calibration activity.'',1; END;');
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_MicroEquipmentStructuredEvidence_Identity ON dbo.MicroEquipmentStructuredEvidence AFTER INSERT AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM inserted i LEFT JOIN dbo.MicroEquipmentActivities a ON a.ActivityID=i.ActivityID WHERE a.ActivityID IS NULL OR a.ActivityType<>i.EvidenceType OR a.ActivityStatus<>N''Submitted'' OR a.PerformedBy<>i.RecordedBy) THROW 56603, ''Equipment evidence must match submitted activity, type and performer.'',1; END;');
