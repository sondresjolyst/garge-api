using System.Linq.Expressions;
using garge_api.Dtos.Common;
using garge_api.Models.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Controllers.Common
{
    public static class PhotoEndpointHelpers
    {
        public static async Task<IActionResult> UpsertAsync<TPhoto>(
            DbContext context,
            DbSet<TPhoto> set,
            Expression<Func<TPhoto, bool>> filter,
            Func<TPhoto> createNew,
            UploadPhotoDto dto,
            string userId)
            where TPhoto : class, IPhotoEntity
        {
            if (string.IsNullOrWhiteSpace(dto.Data) || string.IsNullOrWhiteSpace(dto.ContentType))
                return new BadRequestObjectResult(new { message = "Data and ContentType are required." });

            var existing = await set.FirstOrDefaultAsync(filter);
            if (existing != null)
            {
                existing.Data = dto.Data;
                existing.ContentType = dto.ContentType;
                existing.UserId = userId;
                existing.CreatedAt = DateTime.UtcNow;
            }
            else
            {
                set.Add(createNew());
            }

            await context.SaveChangesAsync();
            return new OkObjectResult(new { message = "Photo saved." });
        }

        public static async Task<IActionResult> GetAsync<TPhoto>(
            DbSet<TPhoto> set,
            Expression<Func<TPhoto, bool>> filter)
            where TPhoto : class, IPhotoEntity
        {
            var photo = await set.FirstOrDefaultAsync(filter);
            if (photo == null)
                return new NotFoundObjectResult(new { message = "No photo found." });

            return new OkObjectResult(new { data = photo.Data, contentType = photo.ContentType });
        }

        public static async Task<IActionResult> DeleteAsync<TPhoto>(
            DbContext context,
            DbSet<TPhoto> set,
            Expression<Func<TPhoto, bool>> filter)
            where TPhoto : class, IPhotoEntity
        {
            var photo = await set.FirstOrDefaultAsync(filter);
            if (photo == null)
                return new NotFoundObjectResult(new { message = "No photo found." });

            set.Remove(photo);
            await context.SaveChangesAsync();
            return new OkObjectResult(new { message = "Photo deleted." });
        }
    }
}
