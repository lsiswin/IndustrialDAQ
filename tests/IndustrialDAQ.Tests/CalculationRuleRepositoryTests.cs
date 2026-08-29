using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Infrastructure;
using IndustrialDAQ.Infrastructure.Processing;
using Microsoft.EntityFrameworkCore;

namespace IndustrialDAQ.Tests;

public sealed class CalculationRuleRepositoryTests
{
    [Fact]
    public async Task Repository_RoundTripsAndDeletesRule()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "industrialdaq-test-" + Guid.NewGuid().ToString("N") + ".db");
        var options = new DbContextOptionsBuilder<DaqDbContext>().UseSqlite("Data Source=" + databasePath).Options;
        var factory = new TestFactory(options);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        var repository = new CalculationRuleRepository(factory);
        await repository.UpsertAsync(new CalculationRule { RuleId = "r1", InputTagNames = ["A"], Expression = "A * 2", TargetTagId = "V", TargetTagName = "Virtual", TargetDataType = TagDataType.Float64 });

        var loaded = Assert.Single(await repository.LoadAsync());
        Assert.Equal("A * 2", loaded.Expression);
        await repository.DeleteAsync("r1");
        Assert.Empty(await repository.LoadAsync());
    }

    private sealed class TestFactory(DbContextOptions<DaqDbContext> options) : IDbContextFactory<DaqDbContext>
    {
        public DaqDbContext CreateDbContext() => new(options);
    }
}
