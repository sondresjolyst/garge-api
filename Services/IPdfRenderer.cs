namespace garge_api.Services
{
    public interface IPdfRenderer
    {
        Task<byte[]> RenderAsync(string html);
    }
}
