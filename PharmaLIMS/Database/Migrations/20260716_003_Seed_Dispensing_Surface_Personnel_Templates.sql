SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.EM_Areas', N'U') IS NULL OR OBJECT_ID(N'dbo.EM_AreaTemplates', N'U') IS NULL
    THROW 51210, 'Required EM area/template tables do not exist.', 1;

;WITH TargetAreas AS
(
    SELECT Id, AreaCode
    FROM dbo.EM_Areas
    WHERE AreaCode IN (N'D29', N'D30')
), TemplateSeed AS
(
    SELECT N'Contact Plate' AS Method, N'CP-BALANCE' AS CodeSuffix, 10 AS SequenceNo
    UNION ALL SELECT N'Contact Plate', N'CP-WORKSURFACE', 20
    UNION ALL SELECT N'Surface Swab', N'SW-CONTROLPANEL', 30
    UNION ALL SELECT N'Surface Swab', N'SW-DOORHANDLE', 40
    UNION ALL SELECT N'Surface Swab', N'SW-BALANCEPAN', 50
    UNION ALL SELECT N'Personnel Monitoring', N'PM-LEFTGLOVE', 60
    UNION ALL SELECT N'Personnel Monitoring', N'PM-RIGHTGLOVE', 70
)
INSERT INTO dbo.EM_AreaTemplates (AreaId, Method, PlateCode, SequenceNo, IsActive)
SELECT
    A.Id,
    S.Method,
    A.AreaCode + N'-' + S.CodeSuffix,
    S.SequenceNo,
    1
FROM TargetAreas A
CROSS JOIN TemplateSeed S
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.EM_AreaTemplates T
    WHERE T.AreaId = A.Id
      AND UPPER(LTRIM(RTRIM(T.Method))) = UPPER(S.Method)
      AND UPPER(LTRIM(RTRIM(T.PlateCode))) = UPPER(A.AreaCode + N'-' + S.CodeSuffix)
);

COMMIT TRANSACTION;

/*
IMPORTANT:
Configure approved Alert/Action limits for Contact Plate, Surface Swab, and Personnel Monitoring
in dbo.EM_GradeLimits according to the approved site SOP and risk assessment before routine use.
The application intentionally does not invent limits.
*/
