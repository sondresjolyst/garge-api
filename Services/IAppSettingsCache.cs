using garge_api.Models.Admin;

namespace garge_api.Services
{
    public interface IAppSettingsCache
    {
        Task<AppSettings> GetAsync();
        void Invalidate();
    }
}
