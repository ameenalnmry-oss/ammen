namespace PharmaLIMS.Models
{
    public class Sample
    {
        public int SampleId { get; set; }
        public string SampleNumber { get; set; } = "";
        public int? PointId { get; set; }
        public string? PointCode { get; set; }
        public string? Location { get; set; }
        public string SampleType { get; set; } = "";
        public string SamplingMethod { get; set; } = "";
        public DateTime SamplingDateTime { get; set; }
        public string SampledBy { get; set; } = "";
        public string Status { get; set; } = "";
        public bool LabelPrinted { get; set; }
        public DateTime CreatedDate { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }

        public List<SampleTest> Tests { get; set; } = new();
    }

    public class SampleTest
    {
        public int SampleTestId { get; set; }
        public int SampleId { get; set; }
        public int TestId { get; set; }
        public string TestName { get; set; } = "";
        public string TestCategory { get; set; } = "";
        public string Unit { get; set; } = "";
        public decimal? AlertLimit { get; set; }
        public decimal? ActionLimit { get; set; }
        public decimal? ResultValue { get; set; }
        public string? Remarks { get; set; }
        public bool? IsPass { get; set; }
        public DateTime? ResultEnteredDate { get; set; }
        public string? EnteredBy { get; set; }
    }
}