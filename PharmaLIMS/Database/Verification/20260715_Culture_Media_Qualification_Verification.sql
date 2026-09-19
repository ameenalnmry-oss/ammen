SET NOCOUNT ON;

SELECT
    COL_LENGTH('dbo.MediaQualificationTests', 'ControlCount') AS ControlCountColumn,
    COL_LENGTH('dbo.MediaQualificationTests', 'TestCount') AS TestCountColumn,
    COL_LENGTH('dbo.MediaQualificationTests', 'RecoveryPercent') AS RecoveryPercentColumn,
    COL_LENGTH('dbo.MediaQualificationTests', 'IncubationConditions') AS IncubationConditionsColumn,
    COL_LENGTH('dbo.MediaQualifications', 'ReleasedBy') AS ReleasedByColumn,
    COL_LENGTH('dbo.MediaQualifications', 'ReviewDate') AS ReviewDateColumn,
    COL_LENGTH('dbo.MediaQualifications', 'ReleaseDate') AS ReleaseDateColumn,
    COL_LENGTH('dbo.MediaQualifications', 'QualificationStatus') AS QualificationStatusColumn;

SELECT TOP (20)
    q.QualificationNo,
    q.PerformedBy,
    q.ReviewedBy,
    q.ReleasedBy,
    q.QualificationStatus,
    q.OverallResult,
    q.ReviewDate,
    q.ReleaseDate,
    l.ReceiptStatus
FROM dbo.MediaQualifications q
INNER JOIN dbo.CultureMediaLots l ON l.MediaLotID = q.MediaLotID
WHERE q.QualificationType = 'Media Lot Promotion Test / Release'
ORDER BY q.MediaQualificationID DESC;
