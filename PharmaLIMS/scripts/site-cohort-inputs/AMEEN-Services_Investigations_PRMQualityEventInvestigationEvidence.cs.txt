#nullable disable
using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Globalization;
namespace PharmaLIMS.Services.Investigations;
public static partial class PRMQualityEventInvestigationService
{
    private const string RawHeaderSql=@"SELECT qe.* FROM dbo.QualityEvents qe WITH(UPDLOCK,HOLDLOCK) WHERE qe.QualityEventID=@QualityEventID AND UPPER(LTRIM(RTRIM(ISNULL(qe.SourceModule,N''))))=N'PRM'";
    private const string RawChecklistSql=@"
SELECT
    @QualityEventID AS QualityEventID,
    q.QuestionID,
    q.SectionName,
    q.QuestionText,
    q.AppliesToEventType,
    q.AppliesToSampleType,
    q.AppliesToTestCategory,
    q.AppliesToTestNameKeyword,
    q.AnswerType,
    q.IsRequired,
    q.ExpectedAnswer,
    q.QuestionLogic,
    q.SortOrder,
    a.AnswerID,
    a.AnswerValue,
    a.Comments,
    CAST(N'' AS nvarchar(120)) AS NAJustification,
    a.AnsweredBy,
    a.AnsweredDate
FROM dbo.QualityEventChecklistQuestions q WITH(HOLDLOCK)
LEFT JOIN dbo.QualityEventChecklistAnswers a WITH(UPDLOCK,HOLDLOCK)
    ON a.QuestionID = q.QuestionID
   AND a.QualityEventID = @QualityEventID
WHERE q.IsActive = 1
  AND EXISTS
  (
      SELECT 1
      FROM dbo.QualityEvents qualityEvent
      WHERE qualityEvent.QualityEventID = @QualityEventID
        AND UPPER(LTRIM(RTRIM(ISNULL(qualityEvent.SourceModule, N'')))) = N'PRM'
  )
  AND
  (
        (q.AppliesToSampleType = 'ALL' AND q.AppliesToTestCategory = 'General Phase I')
     OR (q.AppliesToSampleType = 'PRM' AND q.AppliesToTestCategory = 'PRM General')
     OR (q.AppliesToSampleType = 'PRM' AND q.AppliesToTestCategory = @ChecklistCategory)
  )
ORDER BY
    CASE
        WHEN q.AppliesToTestCategory = 'General Phase I' THEN 0
        WHEN q.AppliesToTestCategory = 'PRM General' THEN 1
        ELSE 2
    END,
    q.SortOrder,
    q.QuestionID";
    private static DataTable QueryInTransaction(SqlConnection connection,SqlTransaction transaction,string sql,params SqlParameter[] parameters)
    { using var command=new SqlCommand(sql,connection,transaction){CommandTimeout=AppConfig.CommandTimeoutSeconds}; command.Parameters.AddRange(parameters);
      using var reader=command.ExecuteReader();var table=new DataTable();table.Load(reader);return table; }
    public static DataTable LoadHeader(SqlConnection connection,SqlTransaction transaction,int qualityEventId)=>
        QueryInTransaction(connection,transaction,RawHeaderSql,new SqlParameter("@QualityEventID",SqlDbType.Int){Value=qualityEventId});
    public static DataTable LoadChecklist(SqlConnection connection,SqlTransaction transaction,int qualityEventId,string categoryText)
    { var table=QueryInTransaction(connection,transaction,RawChecklistSql,new SqlParameter("@QualityEventID",SqlDbType.Int){Value=qualityEventId},
      new SqlParameter("@ChecklistCategory",SqlDbType.NVarChar,120){Value=ResolvePRMChecklistCategory(categoryText)});EnsureChecklistSchema(table);return table; }
    public static string CaptureHistoryJson(SqlConnection connection,SqlTransaction transaction,int qualityEventId,string categoryText,string evidenceTable)
    {
        string sql=evidenceTable switch{"QualityEvents"=>RawHeaderSql,"QualityEventChecklistAnswers"=>RawChecklistSql,_=>throw new InvalidOperationException("Unsupported investigation history table.")};
        using var command=new SqlCommand("SELECT ("+sql+" FOR JSON PATH,INCLUDE_NULL_VALUES);",connection,transaction){CommandTimeout=AppConfig.CommandTimeoutSeconds};
        command.Parameters.Add("@QualityEventID",SqlDbType.Int).Value=qualityEventId;
        if(evidenceTable=="QualityEventChecklistAnswers") command.Parameters.Add("@ChecklistCategory",SqlDbType.NVarChar,120).Value=ResolvePRMChecklistCategory(categoryText);
        return Convert.ToString(command.ExecuteScalar(),CultureInfo.InvariantCulture) ?? "[]";
    }
    public static void RecordHistory(SqlConnection connection,SqlTransaction transaction,int qualityEventId,string categoryText,string evidenceTable,string oldRowsJson,string changedBy,string reason)
    {
        string after=CaptureHistoryJson(connection,transaction,qualityEventId,categoryText,evidenceTable);
        if(string.Equals(oldRowsJson,after,StringComparison.Ordinal)) return;
        if(string.IsNullOrWhiteSpace(changedBy) || string.IsNullOrWhiteSpace(reason) || reason.Length>1000) throw new InvalidOperationException("Investigation history requires an editor and a reason of 1–1000 characters.");
        using var command=new SqlCommand(@"INSERT dbo.QualityEventInvestigationEvidenceHistory(QualityEventID,EvidenceTable,OldRowsJson,NewRowsJson,ChangedBy,ChangedAt,ChangeReason)
VALUES(@ID,@Table,@Before,@After,@User,SYSDATETIME(),@Reason);",connection,transaction){CommandTimeout=AppConfig.CommandTimeoutSeconds};
        command.Parameters.Add("@ID",SqlDbType.Int).Value=qualityEventId;command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=evidenceTable;
        command.Parameters.Add("@Before",SqlDbType.NVarChar,-1).Value=oldRowsJson;command.Parameters.Add("@After",SqlDbType.NVarChar,-1).Value=after;
        command.Parameters.Add("@User",SqlDbType.NVarChar,120).Value=changedBy;command.Parameters.Add("@Reason",SqlDbType.NVarChar,1000).Value=reason;command.ExecuteNonQuery();
    }
    public static void SavePRMQualityEventChecklistAnswers(SqlConnection connection,SqlTransaction transaction,int qualityEventId,DataTable checklistTable,string currentUser,DataTable originalVisibleChecklist=null)
    {
        if(checklistTable==null || qualityEventId<=0 || string.IsNullOrWhiteSpace(currentUser)) throw new InvalidOperationException("Valid investigation, checklist and editor are required.");
        using(var guard=new SqlCommand(@"SELECT COUNT(*) FROM dbo.QualityEvents WITH(UPDLOCK,HOLDLOCK) WHERE QualityEventID=@ID AND UPPER(LTRIM(RTRIM(ISNULL(SourceModule,N''))))=N'PRM'
AND ISNULL(CurrentStatus,N'Open') NOT IN(N'Closed',N'QA Closed',N'Cancelled',N'Rejected Closed');",connection,transaction))
        { guard.CommandTimeout=AppConfig.CommandTimeoutSeconds;guard.Parameters.Add("@ID",SqlDbType.Int).Value=qualityEventId;
          if(Convert.ToInt32(guard.ExecuteScalar(),CultureInfo.InvariantCulture)!=1) throw new InvalidOperationException("The PRM investigation is missing or no longer editable."); }
        using var command=new SqlCommand(@"IF EXISTS(SELECT 1 FROM dbo.QualityEventChecklistAnswers WITH(UPDLOCK,HOLDLOCK) WHERE QualityEventID=@ID AND QuestionID=@Question)
BEGIN UPDATE dbo.QualityEventChecklistAnswers SET AnswerValue=@Answer,Comments=@Comment,AnsweredBy=@User,AnsweredDate=SYSDATETIME()
WHERE QualityEventID=@ID AND QuestionID=@Question AND EXISTS(SELECT CONVERT(varbinary(max),AnswerValue),CONVERT(varbinary(max),Comments)
EXCEPT SELECT CONVERT(varbinary(max),@Answer),CONVERT(varbinary(max),@Comment)); END
ELSE IF @Answer IS NOT NULL OR @Comment IS NOT NULL
INSERT dbo.QualityEventChecklistAnswers(QualityEventID,QuestionID,AnswerValue,Comments,AnsweredBy,AnsweredDate) VALUES(@ID,@Question,@Answer,@Comment,@User,SYSDATETIME());",connection,transaction){CommandTimeout=AppConfig.CommandTimeoutSeconds};
        command.Parameters.Add("@ID",SqlDbType.Int).Value=qualityEventId;var question=command.Parameters.Add("@Question",SqlDbType.Int);
        var answer=command.Parameters.Add("@Answer",SqlDbType.NVarChar,250);var comment=command.Parameters.Add("@Comment",SqlDbType.NVarChar,-1);command.Parameters.Add("@User",SqlDbType.NVarChar,100).Value=currentUser;
        foreach(DataRow row in checklistTable.Rows)
        {
            if(row.RowState==DataRowState.Deleted) throw new DBConcurrencyException("A checklist question was removed.");int id=GetSafeInt(row,"QuestionID");
            if(id<=0) throw new InvalidOperationException("Invalid checklist question.");
            string value=GetSafeString(row,"AnswerValue"),comments=GetSafeString(row,"Comments"),na=GetSafeString(row,"NAJustification");
            if(originalVisibleChecklist!=null)
            { var originals=originalVisibleChecklist.Select("QuestionID="+id.ToString(CultureInfo.InvariantCulture));if(originals.Length!=1) throw new DBConcurrencyException("The checklist question set changed.");
              var original=originals[0];if(value.Equals(GetSafeString(original,"AnswerValue"),StringComparison.Ordinal) && comments.Equals(GetSafeString(original,"Comments"),StringComparison.Ordinal) && na.Equals(GetSafeString(original,"NAJustification"),StringComparison.Ordinal)) continue; }
            if(value.Equals("N/A",StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(na))
            { string prefix="[N/A: "+na.Trim()+"]";if(!comments.TrimStart().StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) comments=prefix+(string.IsNullOrWhiteSpace(comments)?"":" "+comments.Trim()); }
            question.Value=id;answer.Value=string.IsNullOrWhiteSpace(value)?(object)DBNull.Value:value;comment.Value=string.IsNullOrWhiteSpace(comments)?(object)DBNull.Value:comments;command.ExecuteNonQuery();
        }
    }
}
