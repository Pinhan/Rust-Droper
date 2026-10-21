using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace RustDroper.Core.Tests;

internal sealed record RecordedRequest(string Method, Uri Uri, string Body, string? Authorization, string? UserAgent = null);

/// <summary>Ağa çıkmadan HTTP cevabı üreten sahte işleyici; istekleri kaydeder.</summary>
internal sealed class StubHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(
            request.Method.Method,
            request.RequestUri!,
            body,
            request.Headers.TryGetValues("Authorization", out var values) ? values.FirstOrDefault() : null,
            request.Headers.TryGetValues("User-Agent", out var agents) ? string.Join(' ', agents) : null);
        lock (_requests)
        {
            _requests.Add(recorded);
        }

        return respond(recorded);
    }
}

internal static class TestTime
{
    /// <summary>Sahte saati ilerleterek zaman bekleyen (Task.Delay) bir görevin bitmesini sağlar.</summary>
    public static async Task<T> PumpAsync<T>(Task<T> task, FakeTimeProvider fake, int maxSteps = 800)
    {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            fake.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }

        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
