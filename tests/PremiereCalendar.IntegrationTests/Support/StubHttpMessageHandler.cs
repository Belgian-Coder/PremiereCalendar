using System.Net;
using System.Text;

namespace PremiereCalendar.IntegrationTests.Support;

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    private readonly object _gate = new();
    private readonly List<RequestRecord> _requests = [];

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public IReadOnlyList<RequestRecord> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(new RequestRecord(request.Method, request.RequestUri!));
        }

        return Task.FromResult(_responder(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}

internal sealed record RequestRecord(HttpMethod Method, Uri Uri);
