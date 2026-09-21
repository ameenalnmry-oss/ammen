namespace PharmaLIMS.Interfaces
{
    public interface IRepository<T> where T : class
    {
        Task<T?> GetByIdAsync(int id);
        Task<IEnumerable<T>> GetAllAsync();
        Task<int> AddAsync(T entity);
        Task<int> UpdateAsync(T entity);
        Task<int> DeleteAsync(int id);
    }

    public interface IRepository<T, TKey> : IRepository<T> where T : class
    {
        Task<T?> GetByIdAsync(TKey id);
    }
}