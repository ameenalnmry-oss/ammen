SET NOCOUNT ON;

SELECT
    COL_LENGTH(N'dbo.EM_GradeLimits',N'IsActive') AS GradeLimitsIsActive,
    COL_LENGTH(N'dbo.EM_GradeLimits',N'LastModifiedBy') AS GradeLimitsLastModifiedBy,
    COL_LENGTH(N'dbo.EM_GradeLimits',N'LastModifiedAt') AS GradeLimitsLastModifiedAt,
    COL_LENGTH(N'dbo.EM_EventPlates',N'AlertLimitSnapshot') AS AlertLimitSnapshot,
    COL_LENGTH(N'dbo.EM_EventPlates',N'ActionLimitSnapshot') AS ActionLimitSnapshot,
    COL_LENGTH(N'dbo.EM_EventPlates',N'ResultUnitSnapshot') AS ResultUnitSnapshot,
    COL_LENGTH(N'dbo.EM_EventPlates',N'AirVolumeLitersSnapshot') AS AirVolumeLitersSnapshot,
    OBJECT_ID(N'dbo.EM_GradeLimitSignatures',N'U') AS GradeLimitSignaturesTable,
    CASE WHEN EXISTS
    (
        SELECT 1
        FROM sys.indexes AS i
        WHERE i.object_id = OBJECT_ID(N'dbo.EM_GradeLimits')
          AND i.is_unique = 1
          AND i.is_disabled = 0
          AND i.has_filter = 0
          AND
          (
              SELECT COUNT(*)
              FROM sys.index_columns AS ic
              WHERE ic.object_id = i.object_id
                AND ic.index_id = i.index_id
                AND ic.key_ordinal > 0
          ) = 1
          AND EXISTS
          (
              SELECT 1
              FROM sys.index_columns AS ic
              INNER JOIN sys.columns AS c
                  ON c.object_id = ic.object_id
                 AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id
                AND ic.index_id = i.index_id
                AND ic.key_ordinal = 1
                AND c.name = N'Id'
          )
    ) THEN 1 ELSE 0 END AS GradeLimitsIdCandidateKeyReady;

SELECT Grade,Method,AlertLimitTotal,ActionLimitTotal,AirVolumeLiters,IsActive,LastModifiedBy,LastModifiedAt
FROM dbo.EM_GradeLimits
ORDER BY Grade,Method,Id DESC;

SELECT A.Grade,M.Method,L.AlertLimitTotal,L.ActionLimitTotal,L.AirVolumeLiters
FROM (SELECT DISTINCT LTRIM(RTRIM(Grade)) AS Grade FROM dbo.EM_Areas WHERE ISNULL(IsActive,1)=1) A
CROSS JOIN (VALUES(N'Settle Plate'),(N'Active Air Sampling'),(N'Contact Plate'),(N'Surface Swab'),(N'Personnel Monitoring')) M(Method)
OUTER APPLY
(
    SELECT TOP (1) G.AlertLimitTotal,G.ActionLimitTotal,G.AirVolumeLiters
    FROM dbo.EM_GradeLimits G
    WHERE ISNULL(G.IsActive,1)=1
      AND UPPER(LTRIM(RTRIM(ISNULL(G.Grade,N''))))=UPPER(LTRIM(RTRIM(A.Grade)))
      AND UPPER(LTRIM(RTRIM(ISNULL(G.Method,N''))))=UPPER(LTRIM(RTRIM(M.Method)))
    ORDER BY G.Id DESC
) L
WHERE L.AlertLimitTotal IS NULL OR L.ActionLimitTotal IS NULL
ORDER BY A.Grade,M.Method;
