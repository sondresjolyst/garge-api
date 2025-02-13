using Microsoft.EntityFrameworkCore;

namespace garge_api.Models
{
    public partial class ApplicationDbContext(DbContextOptions
        <ApplicationDbContext> options) : DbContext(options)
    {
        public virtual DbSet<Customer> Customer { get; set; }
        public DbSet<User> Users { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>(entity => {
                entity.HasKey(k => k.CustomerId);
            });
            modelBuilder.Entity<User>(entity => {
                entity.HasKey(k => k.Id);
            });
            OnModelCreatingPartial(modelBuilder);
        }

        partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
    }
}