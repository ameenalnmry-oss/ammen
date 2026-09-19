#nullable disable
using System.Data;

namespace PharmaLIMS.Services.Investigations
{
    public class WaterInvestigationChecklistService : IInvestigationChecklistService
    {
        public string ChecklistName => "Database-Driven Quality Event Checklist";

        public DataTable GetChecklist(int qualityEventId)
        {
            if (qualityEventId <= 0)
                return new DataTable();

            DataTable checklist = PharmaLIMS.DatabaseHelper.GetQualityEventChecklist(qualityEventId);
            return checklist ?? new DataTable();
        }
    }
}
