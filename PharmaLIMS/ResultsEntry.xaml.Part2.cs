#nullable disable
using Microsoft.Data.SqlClient;
using System;
using System.Data;

namespace PharmaLIMS
{
    public partial class ResultsEntry
    {
        private void ValidateWaterAnalysisStartInTransaction(
            SqlConnection con,
            SqlTransaction tran,
            DateTime analysisStartedAt)
        {
            using SqlCommand command = new SqlCommand(@"
SELECT SamplingDateTime, ReceivedDateTime, SYSDATETIME() AS ServerNow
FROM dbo.Samples WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID = @sampleId;", con, tran)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };
            command.Parameters.Add("@sampleId", SqlDbType.Int).Value = currentSampleId;

            using SqlDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("The sample no longer exists. Analysis start was cancelled.");
            if (reader.IsDBNull(0))
                throw new InvalidOperationException("Sampling Date / Time is not recorded for this sample.");
            if (reader.IsDBNull(1))
                throw new InvalidOperationException("Received in Lab Date / Time is not recorded for this sample.");

            DateTime samplingDateTime = reader.GetDateTime(0);
            DateTime receivedDateTime = reader.GetDateTime(1);
            DateTime serverNow = reader.GetDateTime(2);

            if (analysisStartedAt < samplingDateTime || analysisStartedAt < receivedDateTime)
                throw new InvalidOperationException("Analysis Start cannot be earlier than Sampling or Received in Lab time.");

            if (analysisStartedAt > serverNow.AddMinutes(1))
                throw new InvalidOperationException("Analysis Start cannot be in the future according to the authoritative database clock.");
        }




        private object ExecuteScalarInTransaction(SqlConnection con, SqlTransaction tran, string sql, params SqlParameter[] parameters)
        {
            using (SqlCommand cmd = new SqlCommand(sql, con, tran))
            {
                cmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;

                if (parameters != null && parameters.Length > 0)
                    cmd.Parameters.AddRange(parameters);

                return cmd.ExecuteScalar();
            }
        }
    }
}
