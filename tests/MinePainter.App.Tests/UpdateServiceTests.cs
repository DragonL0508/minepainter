using MinePainter.App.Services;
using Xunit;

namespace MinePainter.App.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.0", 1, 2, 0)]
    [InlineData("1.10.3", 1, 10, 3)]
    [InlineData("V2.0", 2, 0, 0)]
    public void TryParseVersion_AcceptsTagForms(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateService.TryParseVersion(tag, out var v));
        Assert.Equal(new Version(major, minor, build, 0), v);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData("v1.2.0-beta")]
    public void TryParseVersion_RejectsGarbage(string tag)
    {
        Assert.False(UpdateService.TryParseVersion(tag, out _));
    }
}
