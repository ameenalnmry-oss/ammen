using PharmaLIMS.Models;

namespace PharmaLIMS.Services
{
    public interface IQualityEventService
    {
        Task<QualityEvent?> GetQualityEventByIdAsync(int qualityEventId);
        Task<QualityEvent?> GetOpenQualityEventBySampleIdAsync(int sampleId);
        Task<IEnumerable<QualityEvent>> GetAllQualityEventsAsync();
        Task<int> CreateQualityEventAsync(QualityEvent qualityEvent);
        Task<int> UpdateQualityEventAsync(QualityEvent qualityEvent);
        Task<int> AddAffectedResultAsync(QualityEventAffectedResult result);
        Task<int> AddActionAsync(QualityEventAction action);
        Task<List<QualityEventAffectedResult>> GetAffectedResultsByEventIdAsync(int qualityEventId);
        Task<List<QualityEventAction>> GetActionsByEventIdAsync(int qualityEventId);
    }
}