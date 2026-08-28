using IndustrialDAQ.Infrastructure;
using IndustrialDAQ.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace IndustrialDAQ.UI.Services;

/// <summary>统一记录登录、权限变更和数据写入等关键操作。</summary>
public sealed class SecurityAuditService
{
    private readonly IDbContextFactory<DaqDbContext> _factory;
    public SecurityAuditService(IDbContextFactory<DaqDbContext> factory) => _factory = factory;

    public async Task RecordAsync(string userId, string username, string action, string resourcePath, string detail, bool success)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.SecurityAudits.Add(new SecurityAuditEntity { OccurredAtUtc = DateTime.UtcNow, UserId = userId, Username = username, Action = action, ResourcePath = resourcePath, Detail = detail, Success = success });
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<SecurityAuditEntity>> GetRecentAsync(int count = 200)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.SecurityAudits.AsNoTracking().OrderByDescending(item => item.OccurredAtUtc).Take(count).ToListAsync();
    }
}
