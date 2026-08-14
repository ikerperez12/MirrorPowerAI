using MirrorPowerAI.Windows.Platform;

namespace MirrorPowerAI.Windows.Tests.Platform;

public sealed class GeminiHttpClientFactoryTests
{
    [Fact]
    public void CreateHandler_RedirectsAreDisabledBeforeAnApiKeyCanBeAdded()
    {
        using var handler = GeminiHttpClientFactory.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void Create_UsesAnInfiniteClientTimeoutBecauseEachGeminiRequestOwnsItsTimeout()
    {
        using var client = GeminiHttpClientFactory.Create();

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }
}
