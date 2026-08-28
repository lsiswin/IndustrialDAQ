using System.ComponentModel.DataAnnotations.Schema;

namespace IndustrialDAQ.Infrastructure.Entities;

[Table("security_roles")]
public sealed class RoleEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }
}

[Table("security_user_roles")]
public sealed class UserRoleEntity
{
    public string UserId { get; set; } = string.Empty;
    public string RoleId { get; set; } = string.Empty;
}
