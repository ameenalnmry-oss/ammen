#nullable disable
namespace PharmaLIMS.Services.Investigations
{
    public class InvestigationChecklistFactory
    {
        private readonly IInvestigationChecklistService _databaseChecklistService;

        public InvestigationChecklistFactory()
        {
            _databaseChecklistService = new WaterInvestigationChecklistService();
        }

        public IInvestigationChecklistService GetService(InvestigationChecklistRequest request)
        {
            // All investigation checklist questions are now fixed by test type in the database.
            // The factory remains in place to keep the code split and to avoid breaking the project structure,
            // but it must not select old hard-coded checklist builders.
            return _databaseChecklistService;
        }
    }
}
