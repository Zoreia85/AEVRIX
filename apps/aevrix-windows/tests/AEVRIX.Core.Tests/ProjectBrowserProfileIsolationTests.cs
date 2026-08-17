using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ProjectBrowserProfileIsolationTests
{
    [TestMethod]
    public void ProjectBrowserProfile_SameTargetDifferentProjectsProducesDifferentPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-profile-path-tests", Guid.NewGuid().ToString("N"));
        var paths = new AevrixDataPaths(
            root,
            Path.Combine(root, "Projects"),
            Path.Combine(root, "Vault"),
            Path.Combine(root, "BrowserProfiles"),
            Path.Combine(root, "Engine"),
            Path.Combine(root, "Updates"),
            Path.Combine(root, "Logs"),
            Path.Combine(root, "Cache"));
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();

        var profileA = paths.ProjectBrowserProfile(projectA, "portal-web");
        var profileB = paths.ProjectBrowserProfile(projectB, "portal-web");

        Assert.AreNotEqual(profileA, profileB);
        StringAssert.Contains(profileA, projectA.ToString("N"));
        StringAssert.Contains(profileB, projectB.ToString("N"));
        Assert.AreEqual("portal-web", Path.GetFileName(profileA));
        Assert.AreEqual("portal-web", Path.GetFileName(profileB));
    }

    [TestMethod]
    public void ProjectBrowserProfile_SameProjectDifferentTargetsProducesDifferentPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-profile-path-tests", Guid.NewGuid().ToString("N"));
        var paths = new AevrixDataPaths(
            root,
            Path.Combine(root, "Projects"),
            Path.Combine(root, "Vault"),
            Path.Combine(root, "BrowserProfiles"),
            Path.Combine(root, "Engine"),
            Path.Combine(root, "Updates"),
            Path.Combine(root, "Logs"),
            Path.Combine(root, "Cache"));
        var projectId = Guid.NewGuid();

        var profileA = paths.ProjectBrowserProfile(projectId, "portal-web");
        var profileB = paths.ProjectBrowserProfile(projectId, "admin-web");

        Assert.AreNotEqual(profileA, profileB);
    }

    [TestMethod]
    public void ProjectBrowserProfile_RejectsEmptyProjectId()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-profile-path-tests", Guid.NewGuid().ToString("N"));
        var paths = new AevrixDataPaths(
            root,
            Path.Combine(root, "Projects"),
            Path.Combine(root, "Vault"),
            Path.Combine(root, "BrowserProfiles"),
            Path.Combine(root, "Engine"),
            Path.Combine(root, "Updates"),
            Path.Combine(root, "Logs"),
            Path.Combine(root, "Cache"));

        Assert.ThrowsExactly<ArgumentException>(() => paths.ProjectBrowserProfile(Guid.Empty, "portal-web"));
    }
}
