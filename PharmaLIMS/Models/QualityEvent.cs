namespace PharmaLIMS.Models
{
    public class QualityEvent
    {
        public int QualityEventId { get; set; }
        public string EventNumber { get; set; } = "";
        public string EventType { get; set; } = "";
        public string? Severity { get; set; }
        public int? SampleId { get; set; }
        public string? SampleNumber { get; set; }
        public string CurrentStatus { get; set; } = "";
        public string? DetectedBy { get; set; }
        public DateTime? DetectedDate { get; set; }
        public string? DetectionSource { get; set; }
        public string? InitialDescription { get; set; }
        public string? ImmediateAction { get; set; }
        public string? RootCauseCategory { get; set; }
        public string? RootCauseDetails { get; set; }
        public string? ImpactAssessment { get; set; }
        public bool CAPARequired { get; set; }
        public string? QAConclusion { get; set; }
        public string? FinalDisposition { get; set; }
        public string? ClosedBy { get; set; }
        public DateTime? ClosedDate { get; set; }
        public DateTime CreatedDate { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }

        public List<QualityEventAffectedResult> AffectedResults { get; set; } = new();
        public List<QualityEventAction> Actions { get; set; } = new();
    }

    public class QualityEventAffectedResult
    {
        public int AffectedResultId { get; set; }
        public int QualityEventId { get; set; }
        public int? SampleTestId { get; set; }
        public string? SourceModule { get; set; }
        public int? SourceResultId { get; set; }
        public int? TestId { get; set; }
        public string TestName { get; set; } = "";
        public string? ResultValue { get; set; }
        public string? SpecificationLimit { get; set; }
        public string? Unit { get; set; }
        public string FailureType { get; set; } = "";
        public DateTime CreatedDate { get; set; }
    }

    public class QualityEventAction
    {
        public int QualityEventActionId { get; set; }
        public int QualityEventId { get; set; }
        public string ActionType { get; set; } = "";
        public string? ActionDescription { get; set; }
        public string? PerformedBy { get; set; }
        public DateTime PerformedDate { get; set; }
        public int? ElectronicSignatureId { get; set; }
    }
}