using Microsoft.Data.SqlClient;
using System.Data;
namespace PharmaLIMS.Services;
internal static class CultureQualificationStartContract
{
    internal const string Sql=@"
INSERT INTO dbo.MediaQualifications
(
    QualificationNo, MediaLotID, MediaPreparationID, QualificationType, QualificationDate,
    PerformedBy, ReviewedBy, ReleasedBy, ReviewDate, ReleaseDate,
    QualificationStatus, OverallResult, Remarks,
    QualificationStartedAt, MinimumIncubationHoursSnapshot, IncubationCompletedAt
)
VALUES
(
    @QualificationNo, @MediaLotID, NULL, N'Media Lot Promotion Test / Release', CAST(SYSDATETIME() AS date),
    @PerformedBy, NULL, NULL, NULL, NULL,
    N'In Progress', N'Pending', NULL,
    SYSDATETIME(), @MinimumIncubationHours, NULL
);
SELECT CAST(SCOPE_IDENTITY() AS int);";
    internal static SqlParameter[] Parameters(string number,int lotId,string signedBy,decimal minimumHours) => new[] {
        new SqlParameter("@QualificationNo",SqlDbType.NVarChar,50){Value=number},
        new SqlParameter("@MediaLotID",SqlDbType.Int){Value=lotId},
        new SqlParameter("@PerformedBy",SqlDbType.NVarChar,100){Value=signedBy},
        new SqlParameter("@MinimumIncubationHours",SqlDbType.Decimal){Precision=18,Scale=3,Value=minimumHours}
    };
}
