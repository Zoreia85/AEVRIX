using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ProductHelpCatalogTests
{
    [TestMethod]
    public void Catalog_CoversPrimaryDesktopActions()
    {
        foreach (var id in new[]{"project.new","analysis.start","evidence.open","blueprint.open","engine.status","security.status"})
            Assert.IsFalse(string.IsNullOrWhiteSpace(ProductHelpCatalog.Get(id).Summary));
    }

    [TestMethod]
    public void Catalog_RejectsUnknownIds()
    {
        Assert.ThrowsExactly<KeyNotFoundException>(()=>ProductHelpCatalog.Get("unknown"));
    }
}
