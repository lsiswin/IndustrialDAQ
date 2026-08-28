using IndustrialDAQ.Core.Authorization;
using IndustrialDAQ.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace IndustrialDAQ.Infrastructure.Authorization;

public sealed class UserRepository : IUserRepository
{
    private readonly IDbContextFactory<DaqDbContext> _contextFactory;

    public UserRepository(IDbContextFactory<DaqDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public async Task<User?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == username, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null) return null;
        var roles = await LoadRolesAsync(context, entity.Id, cancellationToken);
        return MapToModel(entity, roles);
    }

    public async Task<User?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null) return null;
        var roles = await LoadRolesAsync(context, entity.Id, cancellationToken);
        return MapToModel(entity, roles);
    }

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entities = await context.Users
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var links = await context.UserRoles.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var roleNames = await context.Roles.AsNoTracking().ToDictionaryAsync(role => role.Id, role => role.Name, cancellationToken).ConfigureAwait(false);
        return entities.Select(entity => MapToModel(entity, links.Where(link => link.UserId == entity.Id).Select(link => roleNames.GetValueOrDefault(link.RoleId)).Where(name => name is not null)!)).ToList();
    }

    public async Task UpsertAsync(User user, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.Users.FirstOrDefaultAsync(u => u.Id == user.Id, cancellationToken).ConfigureAwait(false);

        if (entity is null)
        {
            entity = new UserEntity { Id = user.Id };
            MapToEntity(user, entity);
            context.Users.Add(entity);
        }
        else
        {
            MapToEntity(user, entity);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var oldLinks = await context.UserRoles.Where(link => link.UserId == user.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        context.UserRoles.RemoveRange(oldLinks);
        foreach (var roleName in user.Roles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var role = await context.Roles.FirstOrDefaultAsync(item => item.Name == roleName, cancellationToken).ConfigureAwait(false);
            if (role is not null) context.UserRoles.Add(new UserRoleEntity { UserId = user.Id, RoleId = role.Id });
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken).ConfigureAwait(false);
        if (entity is not null)
        {
            context.Users.Remove(entity);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static User MapToModel(UserEntity entity, IEnumerable<string?> roles)
    {
        return new User
        {
            Id = entity.Id,
            Username = entity.Username,
            PasswordHash = entity.PasswordHash,
            RealName = entity.RealName,
            Roles = roles.Where(role => !string.IsNullOrWhiteSpace(role)).Cast<string>().ToList(),
            CreatedAtUtc = entity.CreatedAtUtc,
            IsActive = entity.IsActive
            ,FailedLoginCount = entity.FailedLoginCount
            ,LockedUntilUtc = entity.LockedUntilUtc
            ,MustChangePassword = entity.MustChangePassword
            ,LastLoginAtUtc = entity.LastLoginAtUtc
        };
    }

    private static async Task<IReadOnlyList<string>> LoadRolesAsync(DaqDbContext context, string userId, CancellationToken cancellationToken)
    {
        return await (from link in context.UserRoles
                      join role in context.Roles on link.RoleId equals role.Id
                      where link.UserId == userId
                      select role.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void MapToEntity(User model, UserEntity entity)
    {
        entity.Username = model.Username;
        entity.PasswordHash = model.PasswordHash;
        entity.RealName = model.RealName;
        entity.CreatedAtUtc = model.CreatedAtUtc;
        entity.IsActive = model.IsActive;
        entity.FailedLoginCount = model.FailedLoginCount;
        entity.LockedUntilUtc = model.LockedUntilUtc;
        entity.MustChangePassword = model.MustChangePassword;
        entity.LastLoginAtUtc = model.LastLoginAtUtc;
    }
}
