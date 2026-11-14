using SSDShredder.Core;
using Xunit;

namespace SSDShredder.Core.Tests;

public class PathPolicyTests
{
    [Theory]
    [InlineData(@"C:\Demo\fixture.bin", true)]
    [InlineData(@"\\server\share\f.bin", false)]
    [InlineData(@"\\?\C:\f.bin", false)]
    [InlineData(@"\\.\PhysicalDrive0", false)]
    [InlineData(@"C:\f.bin:stream", false)]
    [InlineData(@"relative\f.bin", false)]
    [InlineData("", false)]
    public void Accepts_only_absolute_local(string p, bool ok) => Assert.Equal(ok, PathPolicy.IsAcceptable(p, out _));
}
