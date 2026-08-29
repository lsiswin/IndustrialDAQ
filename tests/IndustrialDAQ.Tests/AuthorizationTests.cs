using IndustrialDAQ.Core.Authorization;
using IndustrialDAQ.Core.ResourceTree;

namespace IndustrialDAQ.Tests;

public sealed class AuthorizationTests
{
    [Fact]
    public void Snapshot_UsesInheritanceDenyPriorityAndDefaultDeny()
    {
        var snapshot = PermissionSnapshot.Build([
            new PermissionPolicy { SubjectType = PermissionSubjectType.Role, SubjectId = "Engineer", ResourcePath = new ResourcePath("Devices"), Action = "Write", Effect = PermissionEffect.Allow, Inherit = true },
            new PermissionPolicy { SubjectType = PermissionSubjectType.Role, SubjectId = "Engineer", ResourcePath = new ResourcePath("Devices/Line/Safety"), Action = "Write", Effect = PermissionEffect.Deny, Inherit = true }
        ]);
        var subject = new PermissionSubject { UserId = "u1", RoleIds = new HashSet<string>(["Engineer"]) };
        AuthorizationRequest Request(string path, string action) => new() { Subject = subject, ResourcePath = new ResourcePath(path), Action = action };

        Assert.Equal(PermissionEffect.Allow, snapshot.FindCandidates(Request("Devices/Line/Speed", "Write"))[0].Effect);
        Assert.Equal(PermissionEffect.Deny, snapshot.FindCandidates(Request("Devices/Line/Safety/EStop", "Write"))[0].Effect);
        Assert.Empty(snapshot.FindCandidates(Request("Devices/Line/Speed", "Delete")));
    }
}
