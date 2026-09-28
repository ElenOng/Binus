using Microsoft.EntityFrameworkCore;

namespace Binus.DataAccess;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    // Add DbSet<TEntity> properties here, for example:
    // public DbSet<YourEntity> YourEntities { get; set; }
}
