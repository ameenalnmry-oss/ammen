namespace PharmaLIMS
{
    public class SampleSituation
    {
        public int SampleID { get; set; }
        public string SampleNumber { get; set; } = "";

        public string CurrentStage { get; set; } = "Unknown";

        public int TotalTests { get; set; }
        public int CompletedResults { get; set; }
        public int PendingResults { get; set; }
        public int AlertResults { get; set; }
        public int FailResults { get; set; }

        public bool InvestigationRequired { get; set; }
        public bool HasOpenInvestigation { get; set; }
        public bool HasClosedInvestigation { get; set; }

        public bool IsReviewed { get; set; }
        public bool IsApproved { get; set; }

        public bool HasActiveCOA { get; set; }
        public bool HasCancelledCOA { get; set; }

        public bool CanEnterResults { get; set; }
        public bool CanSubmitForReview { get; set; }
        public bool CanApprove { get; set; }
        public bool CanIssueCOA { get; set; }

        public string InvestigationStatusText { get; set; } = "Not Required";
        public string COAStatusText { get; set; } = "Not Issued";
        public string NextAction { get; set; } = "Review sample workflow status.";
    }
}