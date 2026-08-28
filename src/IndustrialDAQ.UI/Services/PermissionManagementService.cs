using IndustrialDAQ.Core.Authorization;

namespace IndustrialDAQ.UI.Services;

/// <summary>权限策略唯一写入口，保证数据库与运行时快照同步。</summary>
public sealed class PermissionManagementService
{
    private readonly IAuthorizationRepository _repository;
    private readonly IAuthorizationService _authorizationService;
    private readonly SecurityAuditService _auditService;
    private readonly IAuthManager _authManager;

    public PermissionManagementService(IAuthorizationRepository repository, IAuthorizationService authorizationService, SecurityAuditService auditService, IAuthManager authManager) =>
        (_repository, _authorizationService, _auditService, _authManager) = (repository, authorizationService, auditService, authManager);

    public async Task SaveAsync(PermissionPolicy policy)
    {
        if (!_authManager.IsAdministrator)
        {
            await _auditService.RecordAsync(_authManager.CurrentUser.Id, _authManager.CurrentUser.Username, "AuthorizationDenied", policy.ResourcePath.Value, "Action=ManagePermission", false);
            throw new UnauthorizedAccessException("仅管理员可以维护资源权限策略。");
        }
        await _repository.UpsertPolicyAsync(policy);
        await _authorizationService.ReloadAsync();
        await _auditService.RecordAsync(_authManager.CurrentUser.Id, _authManager.CurrentUser.Username, "PermissionPolicySaved", policy.ResourcePath.Value, $"{policy.SubjectType}:{policy.SubjectId};{policy.Action};{policy.Effect};继承={policy.Inherit}", true);
    }

    public async Task DisableAsync(string policyId)
    {
        if (!_authManager.IsAdministrator)
        {
            await _auditService.RecordAsync(_authManager.CurrentUser.Id, _authManager.CurrentUser.Username, "AuthorizationDenied", "/system/permissions", "Action=ManagePermission", false);
            throw new UnauthorizedAccessException("仅管理员可以维护资源权限策略。");
        }
        await _repository.DisablePolicyAsync(policyId);
        await _authorizationService.ReloadAsync();
        await _auditService.RecordAsync(_authManager.CurrentUser.Id, _authManager.CurrentUser.Username, "PermissionPolicyDisabled", "/system/permissions", policyId, true);
    }
}
