using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using VirtuousGateway.Core.Authentication;
using VirtuousGateway.Core.Configuration;
using VirtuousGateway.Infrastructure.Aplos;
using Microsoft.Extensions.Options;
using Xunit;
using VirtuousGateway.Core.Aplos;
using VirtuousGateway.Api.Extensions;

namespace VirtuousGateway.Tests.Aplos;

public sealed class AplosApiClientTests
{

[Fact]
public async Task PostAsync_AuthenticationFailure_ThrowsNotDispatched()
{
    var authenticationService =
        new FailingAuthenticationService();

    var handler =
        new StubHttpMessageHandler(
            _ =>
                Task.FromResult(
                    new HttpResponseMessage(
                        HttpStatusCode.OK)));

    var httpClient =
        new HttpClient(handler);

    var options =
        Options.Create(
            new AplosOptions
            {
                BaseUrl =
                    "https://app.aplos.com/hermes/api/v1"
            });

    var client =
        CreateAplosApiClient(
            httpClient,
            authenticationService,
            options);

    var exception =
        await Assert.ThrowsAsync<AplosPostNotDispatchedException>(
            () =>
                client.PostAsync(
                    "transactions",
                    """{"test":true}"""));

    Assert.Contains(
        "not dispatched",
        exception.Message,
        StringComparison.OrdinalIgnoreCase);

    Assert.Equal(
        0,
        handler.CallCount);
}

[Fact]
public async Task PostAsync_ResponseBodyReadFailure_ThrowsOutcomeUnknown()
{
    var handler =
        new StubHttpMessageHandler(
            _ => Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.OK)
                {
                    Content =
                        new FailingHttpContent()
                }));

    using var httpClient =
        new HttpClient(handler);

    var authenticationService =
        new StubAplosAuthenticationService(
            "test-access-token");

    var options =
        Options.Create(
            new AplosOptions
            {
                BaseUrl =
                    "https://app.aplos.com/hermes/api/v1"
            });

    var client =
        CreateAplosApiClient(
            httpClient,
            authenticationService,
            options);

    var exception =
        await Assert.ThrowsAsync<AplosPostOutcomeUnknownException>(
            () =>
                client.PostAsync(
                    "transactions",
                    """{"note":"test"}"""));

    Assert.IsType<HttpRequestException>(
        exception.InnerException);

    Assert.Contains(
        "response could not be fully read",
        exception.Message,
        StringComparison.Ordinal);

    Assert.Equal(
        1,
        handler.CallCount);
}

    [Fact]
    public async Task GetAsync_SendsBearerTokenAndReturnsResponse()
    {
        var handler = new StubHttpMessageHandler(
            request =>
            {
                Assert.Equal(
                    HttpMethod.Get,
                    request.Method);

                Assert.Equal(
                    "https://app.aplos.com/hermes/api/v1/test-endpoint",
                    request.RequestUri?.ToString());

                Assert.NotNull(
                    request.Headers.Authorization);

                Assert.Equal(
                    "Bearer",
                    request.Headers.Authorization!.Scheme);

                Assert.Equal(
                    "test-access-token",
                    request.Headers.Authorization.Parameter);

                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"status":"ok"}""")
                    });
            });

        using var httpClient =
            new HttpClient(handler);

        var authenticationService =
            new StubAplosAuthenticationService(
                "test-access-token");

        var options = Options.Create(
            new AplosOptions
            {
                BaseUrl =
                    "https://app.aplos.com/hermes/api/v1"
            });

        var client = CreateAplosApiClient(
            httpClient,
            authenticationService,
            options);

        var result =
            await client.GetAsync(
                "test-endpoint");

        Assert.Equal(
            """{"status":"ok"}""",
            result);

        Assert.Equal(
            1,
            authenticationService.CallCount);

        Assert.Equal(
            1,
            handler.CallCount);
    }

    [Fact]
    public async Task GetAsync_ThrowsWhenRelativePathIsEmpty()
    {
        var handler = new StubHttpMessageHandler(
            _ => Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.OK)));

        using var httpClient =
            new HttpClient(handler);

        var authenticationService =
            new StubAplosAuthenticationService(
                "test-access-token");

        var options = Options.Create(
            new AplosOptions
            {
                BaseUrl =
                    "https://app.aplos.com/hermes/api/v1"
            });

        var client = CreateAplosApiClient(
            httpClient,
            authenticationService,
            options);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetAsync(""));
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task PostAsync_RedirectResponseIsNotFollowed(
        HttpStatusCode redirectStatus)
    {
        using var sourceListener =
            new TcpListener(
                IPAddress.Loopback,
                0);
        sourceListener.Start();

        using var destinationListener =
            new TcpListener(
                IPAddress.Loopback,
                0);
        destinationListener.Start();

        var sourceUri =
            GetListenerUri(sourceListener);
        var destinationUri =
            GetListenerUri(destinationListener);
        var redirectUri =
            new Uri(destinationUri, "capture");

        var sourceRequestTask =
            CaptureRequestAndRedirectAsync(
                sourceListener,
                (int)redirectStatus,
                redirectUri);

        using var provider =
            new ServiceCollection()
                .AddGatewayServices(
                    new ConfigurationBuilder().Build())
                .BuildServiceProvider();

        var clientFactory =
            provider.GetRequiredService<IHttpClientFactory>();

        using var unusedGetClient =
            new HttpClient(
                new StubHttpMessageHandler(
                    _ =>
                        throw new InvalidOperationException(
                            "The GET client must not be used by transaction posting.")));

        var apiClient =
            new AplosApiClient(
                unusedGetClient,
                new StubAplosAuthenticationService(
                    "test-access-token"),
                Options.Create(
                    new AplosOptions
                    {
                        BaseUrl =
                            sourceUri.ToString().TrimEnd('/')
                    }),
                clientFactory);

        var exception =
            await Assert.ThrowsAsync<AplosPostRejectedException>(
                () =>
                    apiClient.PostAsync(
                        "transactions",
                        """{"note":"redirect test"}"""));

        var sourceRequest =
            await sourceRequestTask;

        Assert.Equal(
            redirectStatus,
            exception.StatusCode);
        Assert.Contains(
            "POST /transactions ",
            sourceRequest.Headers,
            StringComparison.Ordinal);
        Assert.Equal(
            """{"note":"redirect test"}""",
            sourceRequest.Body);

        await AssertNoRedirectConnectionAsync(
            destinationListener);
    }

    [Fact]
    public async Task PostAsync_SendsBearerTokenJsonBodyAndReturnsResponse()
    {
        const string requestJson =
            """
            {
              "note": "test transaction"
            }
            """;

        var handler = new StubHttpMessageHandler(
            async request =>
            {
                Assert.Equal(
                    HttpMethod.Post,
                    request.Method);

                Assert.Equal(
                    "https://app.aplos.com/hermes/api/v1/transactions",
                    request.RequestUri?.ToString());

                Assert.NotNull(
                    request.Headers.Authorization);

                Assert.Equal(
                    "Bearer",
                    request.Headers.Authorization!.Scheme);

                Assert.Equal(
                    "test-access-token",
                    request.Headers.Authorization.Parameter);

                Assert.NotNull(request.Content);

                Assert.Equal(
                    "application/json",
                    request.Content!.Headers.ContentType?.MediaType);

                var body =
                    await request.Content.ReadAsStringAsync();

                Assert.Equal(
                    requestJson,
                    body);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"status":"created"}""")
                };
            });

        using var httpClient =
            new HttpClient(handler);

        var authenticationService =
            new StubAplosAuthenticationService(
                "test-access-token");

        var options = Options.Create(
            new AplosOptions
            {
                BaseUrl =
                    "https://app.aplos.com/hermes/api/v1"
            });

        var client = CreateAplosApiClient(
            httpClient,
            authenticationService,
            options);

        var result =
            await client.PostAsync(
                "transactions",
                requestJson);

        Assert.Equal(
            """{"status":"created"}""",
            result);

        Assert.Equal(
            1,
            authenticationService.CallCount);

        Assert.Equal(
            1,
            handler.CallCount);
    }

    [Fact]
    public async Task PostAsync_ThrowsWhenJsonContentIsEmpty()
    {
        var handler = new StubHttpMessageHandler(
            _ => Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.OK)));

        using var httpClient =
            new HttpClient(handler);

        var authenticationService =
            new StubAplosAuthenticationService(
                "test-access-token");

        var options = Options.Create(
            new AplosOptions
            {
                BaseUrl =
                    "https://app.aplos.com/hermes/api/v1"
            });

        var client = CreateAplosApiClient(
            httpClient,
            authenticationService,
            options);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.PostAsync(
                "transactions",
                ""));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
public async Task PostAsync_NonSuccessResponse_ThrowsRejectedWithoutExposingResponseBody(
    HttpStatusCode statusCode)
{
    const string sensitiveResponse =
        """
        {
          "error": "Sensitive downstream detail",
          "token": "secret-token-12345"
        }
        """;

    var handler =
        new StubHttpMessageHandler(
            _ => Task.FromResult(
                new HttpResponseMessage(
                    statusCode)
                {
                    Content =
                        new StringContent(
                            sensitiveResponse)
                }));

    using var httpClient =
        new HttpClient(handler);

    var authenticationService =
        new StubAplosAuthenticationService(
            "test-access-token");

    var options =
        Options.Create(
            new AplosOptions
            {
                BaseUrl =
                    "https://app.aplos.com/hermes/api/v1"
            });

    var client =
        CreateAplosApiClient(
            httpClient,
            authenticationService,
            options);

    var exception =
        await Assert.ThrowsAsync<AplosPostRejectedException>(
            () =>
                client.PostAsync(
                    "transactions",
                    """{"note":"test"}"""));

    Assert.Equal(
        statusCode,
        exception.StatusCode);

    Assert.Contains(
        $"HTTP {(int)statusCode}",
        exception.Message,
        StringComparison.Ordinal);

    Assert.DoesNotContain(
        "Sensitive downstream detail",
        exception.Message,
        StringComparison.OrdinalIgnoreCase);

    Assert.DoesNotContain(
        "secret-token-12345",
        exception.Message,
        StringComparison.OrdinalIgnoreCase);
}

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
public async Task PostAsync_ServerError_ThrowsOutcomeUnknown(
    HttpStatusCode statusCode)
{
    var handler =
        new StubHttpMessageHandler(
            _ => Task.FromResult(
                new HttpResponseMessage(
                    statusCode)
                {
                    Content =
                        new StringContent(
                            """{"error":"sensitive server detail"}""")
                }));

    using var httpClient =
        new HttpClient(handler);

    var authenticationService =
        new StubAplosAuthenticationService(
            "test-access-token");

    var options =
        Options.Create(
            new AplosOptions
            {
                BaseUrl =
                    "https://app.aplos.com/hermes/api/v1"
            });

    var client =
        CreateAplosApiClient(
            httpClient,
            authenticationService,
            options);

    var exception =
        await Assert.ThrowsAsync<AplosPostOutcomeUnknownException>(
            () =>
                client.PostAsync(
                    "transactions",
                    """{"note":"test"}"""));

    Assert.Contains(
        $"HTTP {(int)statusCode}",
        exception.Message,
        StringComparison.Ordinal);

    Assert.DoesNotContain(
        "sensitive server detail",
        exception.Message,
        StringComparison.OrdinalIgnoreCase);
}

[Fact]
public async Task PostAsync_TransportFailure_ThrowsOutcomeUnknown()
{
    var handler =
        new StubHttpMessageHandler(
            _ => throw new HttpRequestException(
                "Simulated transport failure."));

    using var httpClient =
        new HttpClient(handler);

    var authenticationService =
        new StubAplosAuthenticationService(
            "test-access-token");

    var options =
        Options.Create(
            new AplosOptions
            {
                BaseUrl =
                    "https://app.aplos.com/hermes/api/v1"
            });

    var client =
        CreateAplosApiClient(
            httpClient,
            authenticationService,
            options);

    var exception =
        await Assert.ThrowsAsync<AplosPostOutcomeUnknownException>(
            () =>
                client.PostAsync(
                    "transactions",
                    """{"note":"test"}"""));

    Assert.IsType<HttpRequestException>(
        exception.InnerException);

    Assert.Equal(
        1,
        handler.CallCount);
}

[Fact]
public async Task PostAsync_CancellationDuringSend_ThrowsOutcomeUnknown()
{
    var handler =
        new StubHttpMessageHandler(
            _ => throw new OperationCanceledException(
                "Simulated cancellation."));

    using var httpClient =
        new HttpClient(handler);

    var authenticationService =
        new StubAplosAuthenticationService(
            "test-access-token");

    var options =
        Options.Create(
            new AplosOptions
            {
                BaseUrl =
                    "https://app.aplos.com/hermes/api/v1"
            });

    var client =
        CreateAplosApiClient(
            httpClient,
            authenticationService,
            options);

    var exception =
        await Assert.ThrowsAsync<AplosPostOutcomeUnknownException>(
            () =>
                client.PostAsync(
                    "transactions",
                    """{"note":"test"}"""));

    Assert.IsType<OperationCanceledException>(
        exception.InnerException);

    Assert.Equal(
        1,
        handler.CallCount);
}

    private static AplosApiClient CreateAplosApiClient(
        HttpClient httpClient,
        IAplosAuthenticationService authenticationService,
        IOptions<AplosOptions> options)
    {
        return new AplosApiClient(
            httpClient,
            authenticationService,
            options,
            new StubHttpClientFactory(httpClient));
    }

    private static Uri GetListenerUri(
        TcpListener listener)
    {
        var endpoint =
            (System.Net.IPEndPoint)listener.LocalEndpoint;

        return new Uri(
            $"http://127.0.0.1:{endpoint.Port}/");
    }

    private static async Task<CapturedHttpRequest>
        CaptureRequestAndRedirectAsync(
            TcpListener listener,
            int statusCode,
            Uri redirectUri)
    {
        using var cancellationSource =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(10));

        using var tcpClient =
            await listener.AcceptTcpClientAsync(
                cancellationSource.Token);

        using var stream =
            tcpClient.GetStream();

        var headerBytes =
            new List<byte>();
        var oneByte =
            new byte[1];

        while (true)
        {
            var bytesRead =
                await stream.ReadAsync(
                    oneByte,
                    cancellationSource.Token);

            if (bytesRead == 0)
            {
                throw new EndOfStreamException(
                    "The local HTTP request ended before its headers were complete.");
            }

            headerBytes.Add(oneByte[0]);

            var count =
                headerBytes.Count;

            if (count >= 4
                && headerBytes[count - 4] == 13
                && headerBytes[count - 3] == 10
                && headerBytes[count - 2] == 13
                && headerBytes[count - 1] == 10)
            {
                break;
            }
        }

        var headers =
            Encoding.ASCII.GetString(
                headerBytes.ToArray());

        var contentLengthHeader =
            headers
                .Split(
                    "\r\n",
                    StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(
                    line =>
                        line.StartsWith(
                            "Content-Length:",
                            StringComparison.OrdinalIgnoreCase));

        var contentLength =
            contentLengthHeader is null
                ? 0
                : int.Parse(
                    contentLengthHeader[
                        "Content-Length:".Length..].Trim());

        var bodyBytes =
            new byte[contentLength];

        if (contentLength > 0)
        {
            await stream.ReadExactlyAsync(
                bodyBytes,
                cancellationSource.Token);
        }

        var response =
            Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {statusCode} Redirect\r\n" +
                $"Location: {redirectUri}\r\n" +
                "Content-Length: 0\r\n" +
                "Connection: close\r\n\r\n");

        await stream.WriteAsync(
            response,
            cancellationSource.Token);
        await stream.FlushAsync(
            cancellationSource.Token);

        return new CapturedHttpRequest(
            headers,
            Encoding.UTF8.GetString(bodyBytes));
    }

    private static async Task AssertNoRedirectConnectionAsync(
        TcpListener listener)
    {
        using var timeoutSource =
            new CancellationTokenSource();
        var acceptTask =
            listener.AcceptTcpClientAsync(
                timeoutSource.Token).AsTask();
        var timeoutTask =
            Task.Delay(
                TimeSpan.FromSeconds(1));

        if (await Task.WhenAny(
                acceptTask,
                timeoutTask) == acceptTask)
        {
            using var unexpectedConnection =
                await acceptTask;

            throw new Xunit.Sdk.XunitException(
                "The transaction POST unexpectedly connected to the redirect destination.");
        }

        timeoutSource.Cancel();

        try
        {
            using var unexpectedConnection =
                await acceptTask;

            throw new Xunit.Sdk.XunitException(
                "The transaction POST unexpectedly connected to the redirect destination.");
        }
        catch (OperationCanceledException exception)
            when (exception.CancellationToken == timeoutSource.Token)
        {
        }
    }

    private sealed record CapturedHttpRequest(
        string Headers,
        string Body);

    private sealed class StubHttpClientFactory
        : IHttpClientFactory
    {
        private readonly HttpClient _httpClient;

        public StubHttpClientFactory(
            HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public HttpClient CreateClient(
            string name)
        {
            return _httpClient;
        }
    }

    private sealed class FailingHttpContent
    : HttpContent
{
    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context)
    {
        return Task.FromException(
            new HttpRequestException(
                "Simulated response body read failure."));
    }

    protected override bool TryComputeLength(
        out long length)
    {
        length = 0;

        return false;
    }
}

    private sealed class StubHttpMessageHandler
        : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            Task<HttpResponseMessage>> _handler;

        public int CallCount { get; private set; }

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;

            return await _handler(request);
        }
    }

    private sealed class StubAplosAuthenticationService
        : IAplosAuthenticationService
    {
        private readonly string _token;

        public int CallCount { get; private set; }

        public StubAplosAuthenticationService(
            string token)
        {
            _token = token;
        }

        public Task<string> GetAccessTokenAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            return Task.FromResult(
                _token);
        }
    }

    private sealed class FailingAuthenticationService
    : IAplosAuthenticationService
    {
        public Task<string> GetAccessTokenAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<string>(
                new InvalidOperationException(
                    "Simulated authentication failure."));
        }
    }
}