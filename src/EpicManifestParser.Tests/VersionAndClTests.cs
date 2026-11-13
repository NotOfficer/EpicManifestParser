using EpicManifestParser.Api;

namespace EpicManifestParser.Tests;

public class VersionAndClTests
{
    [Theory]
    [InlineData("++Fortnite+Release-32.11-CL-38202817-Windows", "32.11", 38202817)]
    [InlineData("1.2.3.4-CL-5", "1.2.3.4", 5)]
    [InlineData("1.2-CL-2147483647", "1.2", int.MaxValue)]
    public void Parses_version_and_cl(string buildVersion, string expectedVersion, int expectedCl)
    {
        Assert.True(ManifestExtensions.TryParseVersionAndCL(buildVersion, out Version? version, out int cl));
        Assert.Equal(Version.Parse(expectedVersion), version);
        Assert.Equal(expectedCl, cl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2.9.2-2874913+++Portal+Release-Live-Windows")]
    // more than four version components: Version.Parse threw an ArgumentException
    [InlineData("1.2.3.4.5-CL-1")]
    // a CL above int.MaxValue: int.Parse threw an OverflowException
    [InlineData("1.2-CL-2147483648")]
    [InlineData("1.2-CL-99999999999")]
    [InlineData("1.99999999999-CL-1")]
    public void Rejects_what_does_not_fit(string buildVersion)
    {
        Assert.False(ManifestExtensions.TryParseVersionAndCL(buildVersion, out Version? version, out int cl));
        Assert.Null(version);
        Assert.Equal(-1, cl);
    }
}
