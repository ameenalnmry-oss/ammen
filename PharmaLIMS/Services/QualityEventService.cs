using PharmaLIMS.Models;
using PharmaLIMS.Repositories;

namespace PharmaLIMS.Services
{
    public class QualityEventService : IQualityEventService
    {
        private readonly QualityEventRepository _qualityEventRepository;

        public QualityEventService(QualityEventRepository qualityEventRepository)
        {
            _qualityEventRepository = qualityEventRepository;
        }

        public async Task<QualityEvent?> GetQualityEventByIdAsync(int qualityEventId)
        {
            return await _qualityEventRepository.GetByIdAsync(qualityEventId);
        }

        public async Task<QualityEvent?> GetOpenQualityEventBySampleIdAsync(int sampleId)
        {
            return await _qualityEventRepository.GetOpenQualityEventBySampleIdAsync(sampleId);
        }

        public async Task<IEnumerable<QualityEvent>> GetAllQualityEventsAsync()
        {
            return await _qualityEventRepository.GetAllAsync();
        }

        public async Task<int> CreateQualityEventAsync(QualityEvent qualityEvent)
        {
            return await _qualityEventRepository.AddAsync(qualityEvent);
        }

        public async Task<int> UpdateQualityEventAsync(QualityEvent qualityEvent)
        {
            return await _qualityEventRepository.UpdateAsync(qualityEvent);
        }

        public async Task<int> AddAffectedResultAsync(QualityEventAffectedResult result)
        {
            return await _qualityEventRepository.AddAffectedResultAsync(result);
        }

        public async Task<int> AddActionAsync(QualityEventAction action)
        {
            return await _qualityEventRepository.AddActionAsync(action);
        }

        public async Task<List<QualityEventAffectedResult>> GetAffectedResultsByEventIdAsync(int qualityEventId)
        {
            return await _qualityEventRepository.GetAffectedResultsByEventIdAsync(qualityEventId);
        }

        public async Task<List<QualityEventAction>> GetActionsByEventIdAsync(int qualityEventId)
        {
            return await _qualityEventRepository.GetActionsByEventIdAsync(qualityEventId);
        }
    }
}