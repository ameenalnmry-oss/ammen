using PharmaLIMS.Models;

namespace PharmaLIMS.Services
{
    public interface ISampleService
    {
        Task<Sample?> GetSampleByIdAsync(int sampleId);
        Task<Sample?> GetSampleByNumberAsync(string sampleNumber);
        Task<IEnumerable<Sample>> GetSamplesByStatusAsync(string status);
        Task<IEnumerable<Sample>> GetAllSamplesAsync();
        Task<int> CreateSampleAsync(Sample sample);
        Task<int> UpdateSampleAsync(Sample sample);
        Task<int> UpdateSampleStatusAsync(int sampleId, string status, string? modifiedBy = null);
        Task<int> AddTestToSampleAsync(int sampleId, int testId);
        Task<int> UpdateTestResultAsync(int sampleTestId, decimal result, string? remarks, bool? isPass, string enteredBy);
        Task<int> GetPendingResultsCountAsync(int sampleId);
        Task<bool> HasOpenQualityEventAsync(int sampleId);
    }
}