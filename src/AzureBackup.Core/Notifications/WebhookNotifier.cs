namespace AzureBackup.Core.Notifications;

public enum WebhookKind { Bark, Generic }

public enum WebhookMethod { Get, Post }

public enum WebhookEvents { Error, Success, Both }

public sealed record WebhookConfig(string Url, WebhookKind Kind, WebhookMethod Method, WebhookEvents Events);

/// <summary>
/// Sends run notifications. Bark: <c>https://api.day.app/&lt;key&gt;</c> — GET appends
/// <c>/title/body</c>, POST sends a JSON body. Generic: GET uses query params, POST a JSON body.
/// </summary>
public static class WebhookNotifier
{
    public static bool ShouldFire(WebhookEvents events, bool success) => events switch
    {
        WebhookEvents.Both => true,
        WebhookEvents.Success => success,
        WebhookEvents.Error => !success,
        _ => false,
    };

    public static string BuildBarkGetUrl(string baseUrl, string title, string body)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        return $"{baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(title)}/{Uri.EscapeDataString(body)}";
    }

    public static string BuildGenericGetUrl(string baseUrl, string title, string body)
    {
        string sep = baseUrl.Contains('?') ? "&" : "?";
        return $"{baseUrl}{sep}title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
    }

    public static async Task SendAsync(HttpClient http, WebhookConfig config, string title, string body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(config);

        if (config.Method == WebhookMethod.Get)
        {
            string url = config.Kind == WebhookKind.Bark
                ? BuildBarkGetUrl(config.Url, title, body)
                : BuildGenericGetUrl(config.Url, title, body);
            using HttpResponseMessage resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
        }
        else
        {
            var payload = new Dictionary<string, string> { ["title"] = title, ["body"] = body };
            using var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage resp = await http.PostAsync(config.Url, content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
        }
    }
}
