namespace PharmaLIMS.Interfaces
{
    public interface IPrmSpecificationRepository
    {
        int EnsureSampleTestsAssigned(int sampleId);
        int ReplaceUnenteredSampleTests(int sampleId);
    }
}
