#nullable disable
using System.Data;

namespace PharmaLIMS.Services.Investigations
{
    public class InvestigationChecklistItem
    {
        public int SequenceNo { get; set; }
        public string Section { get; set; } = "";
        public string Question { get; set; } = "";
        public bool IsMandatory { get; set; } = true;
        public string ExpectedEvidence { get; set; } = "";
    }

    public class InvestigationChecklistRequest
    {
        public string SourceModule { get; set; } = "";
        public string SampleType { get; set; } = "";
        public string TestName { get; set; } = "";
        public string ResultStatus { get; set; } = "";
        public string DeviationType { get; set; } = "";
    }

    public interface IInvestigationChecklistService
    {
        string ChecklistName { get; }

        // Source of truth: dbo.QualityEventChecklistQuestions through DatabaseHelper.GetQualityEventChecklist.
        // The service must not generate hard-coded legacy checklist questions.
        DataTable GetChecklist(int qualityEventId);
    }
}
