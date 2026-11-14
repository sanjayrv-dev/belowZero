using System.Text.Json;
using SSDShredder.UI.Activation;

namespace SSDShredder.UI.Tests;

public class ActivationEnvelopeTests
{
    [Fact]
    public void Open_WithEmptyPaths_IsStructurallyValid()
    {
        var envelope = ActivationEnvelope.CreateOpen(ActivationSource.Desktop);

        Assert.True(envelope.IsStructurallyValid());
    }

    [Fact]
    public void Open_WithAPath_IsNotStructurallyValid()
    {
        var envelope = ActivationEnvelope.CreateOpen(ActivationSource.Desktop) with { Paths = ["/tmp/x"] };

        Assert.False(envelope.IsStructurallyValid());
    }

    [Fact]
    public void Select_WithExactlyOneAbsolutePath_IsStructurallyValid()
    {
        var envelope = ActivationEnvelope.CreateSelect(ActivationSource.Terminal, "/tmp/fixture.bin");

        Assert.True(envelope.IsStructurallyValid());
    }

    [Theory]
    [InlineData("relative/path.bin")]
    [InlineData(@"\\server\share\file.bin")]
    [InlineData("")]
    public void Select_WithNonAbsoluteOrUncPath_IsNotStructurallyValid(string path)
    {
        var envelope = ActivationEnvelope.CreateSelect(ActivationSource.Terminal, path);

        Assert.False(envelope.IsStructurallyValid());
    }

    [Fact]
    public void Select_WithMoreThanOnePath_IsNotStructurallyValid()
    {
        var envelope = ActivationEnvelope.CreateSelect(ActivationSource.Explorer, "/tmp/a") with
        {
            Paths = ["/tmp/a", "/tmp/b"],
        };

        Assert.False(envelope.IsStructurallyValid());
    }

    [Fact]
    public void RoundTrips_Through_Json_With_Spec_Field_Names()
    {
        var envelope = ActivationEnvelope.CreateSelect(ActivationSource.Explorer, "/tmp/fixture.bin");

        var json = JsonSerializer.Serialize(envelope);
        using var parsed = JsonDocument.Parse(json);

        Assert.Equal(1, parsed.RootElement.GetProperty("v").GetInt32());
        Assert.Equal(envelope.Id, parsed.RootElement.GetProperty("id").GetString());
        Assert.Equal("select", parsed.RootElement.GetProperty("action").GetString());
        Assert.Equal("explorer", parsed.RootElement.GetProperty("source").GetString());

        var roundTripped = JsonSerializer.Deserialize<ActivationEnvelope>(json);
        Assert.NotNull(roundTripped);
        Assert.Equal(envelope.Version, roundTripped.Version);
        Assert.Equal(envelope.Id, roundTripped.Id);
        Assert.Equal(envelope.Action, roundTripped.Action);
        Assert.Equal(envelope.Source, roundTripped.Source);
        Assert.Equal(envelope.Paths, roundTripped.Paths); // sequence equality, not reference equality.
    }

    [Fact]
    public void Ack_Carries_Same_Version_And_Id()
    {
        var envelope = ActivationEnvelope.CreateOpen(ActivationSource.Desktop);

        var ack = ActivationAck.For(envelope, accepted: true);

        Assert.Equal(envelope.Version, ack.Version);
        Assert.Equal(envelope.Id, ack.Id);
        Assert.True(ack.Accepted);
    }
}
