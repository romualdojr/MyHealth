using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MyHealth.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c> to build migrations; no database connection is opened.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();
        DependencyInjection.ConfigureDbContext(options.UseNpgsql("Host=localhost;Database=myhealth_design"));
        return new ApplicationDbContext(options.Options);
    }
}
