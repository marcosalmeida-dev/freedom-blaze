using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using FreedomBlaze.Clients;

namespace FreedomBlaze.Tests;

public sealed class OpenAiNewsRetryPolicyTests
{
    [Theory]
    [InlineData("insufficient_quota")]
    [InlineData("credit_balance_exhausted")]
    [InlineData("billing_hard_limit_reached")]
    [InlineData("organization_spend_limit_exceeded")]
    [InlineData("project_spend_limit_exceeded")]
    [InlineData("organization_usage_limit_exceeded")]
    public async Task QuotaIdentifiersInEitherCodeOrTypeAreNotRetried(string identifier)
    {
        foreach (var property in new[] { "code", "type" })
        {
            var body = JsonSerializer.Serialize(new
            {
                error = new Dictionary<string, string> { [property] = identifier },
            });
            using var fixture = new PipelineFixture(429, body);
            using var message = fixture.CreateMessage();

            await fixture.Pipeline.SendAsync(message);

            Assert.Equal(1, fixture.Handler.RequestCount);
            Assert.Equal(429, message.Response!.Status);
            Assert.Equal(body, message.Response.Content.ToString());
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task QuotaBodyIsPreservedForSynchronousAndAsynchronousCalls(bool useAsync, bool bufferResponse)
    {
        const string body = """{"error":{"code":"credit_balance_exhausted","type":"insufficient_quota"}}""";
        using var fixture = new PipelineFixture(429, body);
        using var message = fixture.CreateMessage();
        message.BufferResponse = bufferResponse;

        if (useAsync)
        {
            await fixture.Pipeline.SendAsync(message);
        }
        else
        {
            fixture.Pipeline.Send(message);
        }

        Assert.Equal(1, fixture.Handler.RequestCount);
        Assert.Equal(body, message.Response!.Content.ToString());
    }

    [Theory]
    [InlineData(429, """{"error":{"code":"rate_limit_exceeded","type":"rate_limit_error"}}""")]
    [InlineData(429, """{"error":{"code":"slow_down","type":"rate_limit_error"}}""")]
    [InlineData(429, """{"error":{"message":"Not an insufficient_quota error","code":null}}""")]
    [InlineData(429, "<html>Too many requests</html>")]
    [InlineData(429, """{"error":{"code":429,"type":[]}}""")]
    [InlineData(503, """{"error":{"code":"server_is_overloaded"}}""")]
    [InlineData(500, """{"error":{"code":"insufficient_quota"}}""")]
    public async Task TransientAndUnrecognizedErrorsKeepSdkRetries(int status, string body)
    {
        using var fixture = new PipelineFixture(status, body);
        using var message = fixture.CreateMessage();

        await fixture.Pipeline.SendAsync(message);

        Assert.Equal(2, fixture.Handler.RequestCount);
        Assert.Equal(200, message.Response!.Status);
    }

    [Fact]
    public async Task NonRetriableErrorsKeepSdkClassification()
    {
        using var fixture = new PipelineFixture(401, """{"error":{"code":"invalid_api_key"}}""");
        using var message = fixture.CreateMessage();

        await fixture.Pipeline.SendAsync(message);

        Assert.Equal(1, fixture.Handler.RequestCount);
        Assert.Equal(401, message.Response!.Status);
    }

    private sealed class PipelineFixture : IDisposable
    {
        private readonly HttpClient _httpClient;

        public PipelineFixture(int status, string body)
        {
            Handler = new StubHandler(status, body);
            _httpClient = new HttpClient(Handler);
            Pipeline = ClientPipeline.Create(new ClientPipelineOptions
            {
                RetryPolicy = new OpenAiNewsRetryPolicy(maxRetries: 1),
                Transport = new HttpClientPipelineTransport(_httpClient),
            });
        }

        public StubHandler Handler { get; }
        public ClientPipeline Pipeline { get; }

        public PipelineMessage CreateMessage()
        {
            var message = Pipeline.CreateMessage();
            message.Request.Method = "POST";
            message.Request.Uri = new Uri("https://openai.invalid/v1/responses");
            return message;
        }

        public void Dispose() => _httpClient.Dispose();
    }

    private sealed class StubHandler(int initialStatus, string initialBody) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return new HttpResponseMessage((HttpStatusCode)(RequestCount == 1 ? initialStatus : 200))
            {
                Content = new StringContent(RequestCount == 1 ? initialBody : "{}", Encoding.UTF8, "application/json"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }
}
