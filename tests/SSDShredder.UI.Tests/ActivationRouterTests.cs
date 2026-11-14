using SSDShredder.UI.Activation;

namespace SSDShredder.UI.Tests;

public class ActivationRouterTests
{
    private static string UniqueChannel() => $"test-{Guid.NewGuid():N}";

    [Fact]
    public async Task TrySendToExistingOwner_WithNoOwnerListening_ReturnsFalse()
    {
        var envelope = ActivationEnvelope.CreateOpen(ActivationSource.Desktop);

        var sent = await ActivationRouter.TrySendToExistingOwnerAsync(envelope, UniqueChannel());

        Assert.False(sent);
    }

    [Fact]
    public async Task Owner_Receives_And_Acknowledges_One_Forwarded_Activation()
    {
        var channel = UniqueChannel();
        ActivationEnvelope? received = null;
        await using var router = new ActivationRouter(
            (envelope, _) =>
            {
                received = envelope;
                return Task.FromResult(true);
            },
            channel);
        router.StartOwning();

        var envelope = ActivationEnvelope.CreateSelect(ActivationSource.Terminal, "/tmp/fixture.bin");
        var accepted = await ActivationRouter.TrySendToExistingOwnerAsync(envelope, channel);

        Assert.True(accepted);
        Assert.NotNull(received);
        Assert.Equal(envelope.Id, received!.Id);
        Assert.Equal(envelope.Paths, received.Paths);
    }

    [Fact]
    public async Task Owner_Rejects_Structurally_Invalid_Envelope()
    {
        var channel = UniqueChannel();
        var callbackInvoked = false;
        await using var router = new ActivationRouter(
            (_, _) =>
            {
                callbackInvoked = true;
                return Task.FromResult(true);
            },
            channel);
        router.StartOwning();

        // "open" must carry an empty paths array per the spec; this one doesn't.
        var invalidEnvelope = ActivationEnvelope.CreateOpen(ActivationSource.Desktop) with { Paths = ["/tmp/x"] };
        var accepted = await ActivationRouter.TrySendToExistingOwnerAsync(invalidEnvelope, channel);

        Assert.False(accepted);
        Assert.False(callbackInvoked);
    }

    [Fact]
    public async Task Owner_Deduplicates_Repeated_Activation_Id()
    {
        var channel = UniqueChannel();
        var callCount = 0;
        await using var router = new ActivationRouter(
            (_, _) =>
            {
                callCount++;
                return Task.FromResult(true);
            },
            channel);
        router.StartOwning();

        var envelope = ActivationEnvelope.CreateOpen(ActivationSource.Desktop);

        var firstAccepted = await ActivationRouter.TrySendToExistingOwnerAsync(envelope, channel);
        var secondAccepted = await ActivationRouter.TrySendToExistingOwnerAsync(envelope, channel);

        Assert.True(firstAccepted);
        Assert.False(secondAccepted); // same id delivered twice: second delivery is not re-executed.
        Assert.Equal(1, callCount);
    }
}
