using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.OpenAI;
using Xunit;

namespace AgentSmith.Tests;

public sealed class ChatGptRetryTests
{
    private const string Token = "seeded-private-access-token";
    private const string ErrorBody = "seeded-private-provider-error-body";
    private const string Delta = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}\n\n";
    private const string Completed = "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hello\"}]}]}}\n\n";

    [Theory]
    [InlineData(500, false)]
    [InlineData(502, false)]
    [InlineData(503, false)]
    [InlineData(504, false)]
    [InlineData(503, true)]
    public async Task TransientStatusThenCompletedSseRetriesSuccessfully(int status, bool dateHeader)
    {
        using var handler = new RecordingHandler((attempt, _, _) => Task.FromResult(attempt == 1
            ? Failure(status, dateHeader ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-1)) : ZeroDelay())
            : Success()));
        using var http = new HttpClient(handler);
        var events = await CollectAsync(Client(http), Request());

        Assert.Equal(2, handler.Attempts.Count);
        Assert.Equal("Hello", Assert.Single(events, e => e.Type == "text").Text);
        var completed = Assert.Single(events, e => e.Type == "completed");
        Assert.Equal("Hello", Assert.Single(completed.Output!)["content"]![0]!["text"]!.ToString());
        Assert.Equal("completed", events.Last().Type);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task TransientStatusExhaustsExactlyThreeAttemptsWithoutLeakingBody(int status)
    {
        using var handler = new RecordingHandler((_, _, _) => Task.FromResult(Failure(status, ZeroDelay())));
        using var http = new HttpClient(handler);
        var events = new List<ModelEvent>();
        var error = await Assert.ThrowsAsync<UserFacingException>(() => CollectAsync(Client(http), Request(), events: events));

        Assert.Equal(3, handler.Attempts.Count);
        Assert.Empty(events);
        Assert.Contains($"HTTP {status}", error.Message);
        Assert.DoesNotContain(ErrorBody, error.ToString());
        Assert.DoesNotContain(Token, error.ToString());
    }

    [Fact]
    public async Task EveryAttemptUsesFreshRequestAndContentWithIdenticalPayloadAndAuthorization()
    {
        using var handler = new RecordingHandler((attempt, _, _) => Task.FromResult(attempt < 3
            ? Failure(503, ZeroDelay()) : Success()));
        using var http = new HttpClient(handler);
        var request = Request();
        var expectedBody = ChatGptModelClient.BuildRequest(request).ToJsonString();
        await CollectAsync(Client(http), request);

        Assert.Equal(3, handler.Attempts.Count);
        for (var i = 0; i < handler.Attempts.Count; i++)
        {
            var attempt = handler.Attempts[i];
            Assert.Equal(HttpMethod.Post, attempt.Method);
            Assert.EndsWith("/responses", attempt.Uri!.AbsoluteUri);
            Assert.Equal(expectedBody, attempt.Body);
            Assert.Equal("Bearer " + Token, attempt.Authorization);
            Assert.Equal("application/json", attempt.MediaType);
            Assert.Equal(handler.Attempts[0].Uri, attempt.Uri);
            for (var j = 0; j < i; j++)
            {
                Assert.NotSame(handler.Attempts[j].Request, attempt.Request);
                Assert.NotSame(handler.Attempts[j].Content, attempt.Content);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringRetryDelayStopsWithoutSendingAnotherRequest(bool explicitRetryAfter)
    {
        var firstResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RecordingHandler((_, _, _) =>
        {
            var response = Failure(503, explicitRetryAfter ? new RetryConditionHeaderValue(TimeSpan.FromSeconds(10)) : null);
            firstResponse.TrySetResult(true);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var events = new List<ModelEvent>();
        var collecting = CollectAsync(Client(http), Request(), cancellation.Token, events);
        await firstResponse.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Let the successful SendAsync return and enter the actual (non-injected) retry delay.
        await Task.Delay(100);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collecting.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Single(handler.Attempts);
        Assert.Empty(events);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(422)]
    [InlineData(429)]
    public async Task PermanentStatusIsNotRetriedEvenWithZeroRetryAfter(int status)
    {
        using var handler = new RecordingHandler((_, _, _) => Task.FromResult(Failure(status, ZeroDelay())));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<UserFacingException>(() => CollectAsync(Client(http), Request()));

        Assert.Single(handler.Attempts);
        Assert.Contains($"HTTP {status}", error.Message);
        Assert.DoesNotContain(ErrorBody, error.ToString());
        Assert.DoesNotContain(Token, error.ToString());
    }

    [Fact]
    public async Task TransportExceptionBeforeResponseIsNotRetried()
    {
        var disconnected = new HttpRequestException("Synthetic connection failure");
        using var handler = new RecordingHandler((_, _, _) => Task.FromException<HttpResponseMessage>(disconnected));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => CollectAsync(Client(http), Request()));

        Assert.Same(disconnected, error);
        Assert.Single(handler.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialOutputThenDisconnectNeverRestartsTheRequest(bool readException)
    {
        using var handler = new RecordingHandler((attempt, _, _) => Task.FromResult(attempt == 1
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new DisconnectingStream(Encoding.UTF8.GetBytes(Delta), readException))
            }
            : Success()));
        using var http = new HttpClient(handler);
        var events = new List<ModelEvent>();
        var error = await Record.ExceptionAsync(() => CollectAsync(Client(http), Request(), events: events));

        if (readException) Assert.IsType<IOException>(error);
        else
        {
            var disconnected = Assert.IsType<UserFacingException>(error);
            Assert.Contains("disconnected before completion", disconnected.Message);
        }
        Assert.Single(handler.Attempts);
        Assert.Equal("Hello", Assert.Single(events).Text);
        Assert.DoesNotContain(events, e => e.Type == "completed" || e.Output is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryAfterOverThirtySecondsFailsPromptlyWithoutRetryingEarly(bool dateHeader)
    {
        using var handler = new RecordingHandler((_, _, _) => Task.FromResult(Failure(503,
            dateHeader ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(1))
                : new RetryConditionHeaderValue(TimeSpan.FromSeconds(31)))));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<UserFacingException>(() =>
            CollectAsync(Client(http), Request(), cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Single(handler.Attempts);
        Assert.Contains("HTTP 503", error.Message);
        Assert.True(error.Message.Contains("wait", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("later", StringComparison.OrdinalIgnoreCase),
            "An excessive Retry-After must give honest wait/retry-later advice, not silently retry early.");
        Assert.DoesNotContain(ErrorBody, error.ToString());
        Assert.DoesNotContain(Token, error.ToString());
    }

    private static ChatGptModelClient Client(HttpClient http) => new(http, _ => Task.FromResult(Token));

    private static ModelRequest Request() => new("model", "instructions",
        [new JsonObject { ["role"] = "user", ["content"] = "A stable request payload" }], []);

    private static RetryConditionHeaderValue ZeroDelay() => new(TimeSpan.Zero);

    private static HttpResponseMessage Failure(int status, RetryConditionHeaderValue? retryAfter)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(ErrorBody) };
        response.Headers.RetryAfter = retryAfter;
        return response;
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(Delta + Completed, Encoding.UTF8, "text/event-stream")
    };

    private static async Task<List<ModelEvent>> CollectAsync(ChatGptModelClient client, ModelRequest request,
        CancellationToken cancellationToken = default, List<ModelEvent>? events = null)
    {
        events ??= [];
        await foreach (var item in client.StreamAsync(request, cancellationToken)) events.Add(item);
        return events;
    }

    private sealed record Attempt(HttpRequestMessage Request, HttpContent Content, string Body,
        string? Authorization, string? MediaType, HttpMethod Method, Uri? Uri);

    private sealed class RecordingHandler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Attempt> Attempts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content!;
            Attempts.Add(new(request, content, await content.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization?.ToString(), content.Headers.ContentType?.MediaType, request.Method, request.RequestUri));
            return await respond(Attempts.Count, request, cancellationToken);
        }
    }

    private sealed class DisconnectingStream(byte[] prefix, bool readException) : MemoryStream(prefix)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == Length && readException) throw new IOException("Synthetic response-stream disconnect");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
