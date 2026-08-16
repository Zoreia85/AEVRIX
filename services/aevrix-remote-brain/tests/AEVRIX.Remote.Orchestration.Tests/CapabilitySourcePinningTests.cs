using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class CapabilitySourcePinningTests
{
    [TestMethod]
    public void Validate_RejectsAbbreviatedGitRevision()
    {
        var source = ValidSource() with { PinnedRevision = "0123456" };

        Assert.Throws<ArgumentException>(() => source.Validate());
    }

    [TestMethod]
    public void Validate_RejectsZeroRevisionAndZeroContentDigest()
    {
        var zeroRevision = ValidSource() with { PinnedRevision = new string('0', 40) };
        var zeroContent = ValidSource() with { ContentSha256 = new string('0', 64) };

        Assert.Throws<ArgumentException>(() => zeroRevision.Validate());
        Assert.Throws<ArgumentException>(() => zeroContent.Validate());
    }

    [TestMethod]
    public void Validate_RejectsNullRevisionAndContentDigestFailClosed()
    {
        var nullRevision = ValidSource() with { PinnedRevision = null! };
        var nullContent = ValidSource() with { ContentSha256 = null! };

        Assert.Throws<ArgumentException>(() => nullRevision.Validate());
        Assert.Throws<ArgumentException>(() => nullContent.Validate());
    }

    [TestMethod]
    public void Validate_AcceptsFullSha1AndSha256ObjectIds()
    {
        ValidSource().Validate();
        (ValidSource() with
        {
            PinnedRevision = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        }).Validate();
    }

    private static CapabilitySource ValidSource() => new(
        RepositoryFullName: "example/tool",
        SpdxLicense: "MIT",
        PinnedRevision: "0123456789abcdef0123456789abcdef01234567",
        ContentSha256: new string('a', 64));
}
