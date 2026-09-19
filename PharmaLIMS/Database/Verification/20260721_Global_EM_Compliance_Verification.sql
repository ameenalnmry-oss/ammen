SET NOCOUNT ON;

SELECT
    COL_LENGTH(N'dbo.EM_Schedules',N'ApprovalStatus') AS ScheduleApprovalStatus,
    COL_LENGTH(N'dbo.EM_Schedules',N'VersionNo') AS ScheduleVersion,
    COL_LENGTH(N'dbo.EM_Schedules',N'EffectiveFrom') AS ScheduleEffectiveFrom,
    COL_LENGTH(N'dbo.EM_PlanSamples',N'MediaPreparationID') AS MediaPreparationTraceability,
    COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase1Start') AS IncubationPhase1,
    COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase2Start') AS IncubationPhase2,
    COL_LENGTH(N'dbo.EM_PlanSamples',N'ControlReadAt') AS NegativeControlReadTraceability;

SELECT A.AreaCode,A.AreaName,A.Grade,T.Method,COUNT(*) AS ActiveTemplateCount
FROM dbo.EM_Areas A
LEFT JOIN dbo.EM_AreaTemplates T ON T.AreaId=A.Id AND ISNULL(T.IsActive,1)=1
WHERE ISNULL(A.IsActive,1)=1
GROUP BY A.AreaCode,A.AreaName,A.Grade,T.Method
ORDER BY A.AreaCode,T.Method;

SELECT A.Grade,M.Method,L.AlertLimitTotal,L.ActionLimitTotal,L.AirVolumeLiters
FROM (SELECT DISTINCT Grade FROM dbo.EM_Areas WHERE ISNULL(IsActive,1)=1) A
CROSS JOIN (VALUES(N'Settle Plate'),(N'Active Air Sampling'),(N'Contact Plate'),(N'Surface Swab'),(N'Personnel Monitoring')) M(Method)
LEFT JOIN dbo.EM_GradeLimits L ON L.Grade=A.Grade AND L.Method=M.Method
WHERE L.Id IS NULL OR L.AlertLimitTotal IS NULL OR L.ActionLimitTotal IS NULL
ORDER BY A.Grade,M.Method;

SELECT ScheduleID,ScheduleName,Frequency,Method,ApprovalStatus,VersionNo,EffectiveFrom,IsActive
FROM dbo.EM_Schedules
ORDER BY IsActive DESC,ScheduleName;
