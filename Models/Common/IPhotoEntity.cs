namespace garge_api.Models.Common
{
    public interface IPhotoEntity
    {
        int Id { get; set; }
        string UserId { get; set; }
        string Data { get; set; }
        string ContentType { get; set; }
        DateTime CreatedAt { get; set; }
    }
}
