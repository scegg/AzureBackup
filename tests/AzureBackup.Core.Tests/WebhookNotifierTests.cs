using AzureBackup.Core.Notifications;
using Xunit;

namespace AzureBackup.Core.Tests;

public class WebhookNotifierTests
{
    [Fact]
    public void Bark_url_encodes_title_and_body()
    {
        string url = WebhookNotifier.BuildBarkGetUrl("https://api.day.app/KEY", "Backup OK", "1 file / done");
        Assert.Equal("https://api.day.app/KEY/Backup%20OK/1%20file%20%2F%20done", url);
    }

    [Fact]
    public void Bark_url_trims_trailing_slash()
    {
        string url = WebhookNotifier.BuildBarkGetUrl("https://api.day.app/KEY/", "t", "b");
        Assert.Equal("https://api.day.app/KEY/t/b", url);
    }

    [Fact]
    public void Generic_get_uses_query_params()
    {
        string url = WebhookNotifier.BuildGenericGetUrl("https://hook.example/notify", "T i", "B&y");
        Assert.Equal("https://hook.example/notify?title=T%20i&body=B%26y", url);
    }

    [Theory]
    [InlineData(WebhookEvents.Both, true, true)]
    [InlineData(WebhookEvents.Both, false, true)]
    [InlineData(WebhookEvents.Error, false, true)]
    [InlineData(WebhookEvents.Error, true, false)]
    [InlineData(WebhookEvents.Success, true, true)]
    [InlineData(WebhookEvents.Success, false, false)]
    public void ShouldFire_respects_event_filter(WebhookEvents events, bool success, bool expected)
        => Assert.Equal(expected, WebhookNotifier.ShouldFire(events, success));
}
