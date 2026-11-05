using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ApiToolkit.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using System.Text;
using Newtonsoft.Json;
using System.Diagnostics;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Builder;

public class RedactTests
{
  [Test]
  public void TestRedact_Success()
  {
    // Arrange
    string jsonString = @"
            {
                'name': 'John Doe',
                'addresses': [{
                    'street': '123 Main St',
                    'city': {'name':'Anytown'},
                    'state': 'CA',
                    'house_no': {'number':'5A', 'floor': 'floor'},
                    'zip': ['12345', '0987']
                }],
                'phone': '555-555-1212',
                'email': 'jdoe@example.com'
            }";
    List<string> jsonPaths = new List<string>
        {
            "$.name",
            "$.addresses[*].street",
            "$.addresses[*].city.name",
            "$.phone",
            "$.addresses[*].house_no",
            "$.addresses[*].zip[*]",
            "$.invalid.path" // NotFound paths should not affect final json
        };

    // Act
    byte[] result = Client.RedactFields(jsonString, jsonPaths);

    // Assert
    JObject expectedObject = JObject.Parse(@"
            {
                'name': '[CLIENT_REDACTED]',
                'addresses': [{
                    'street': '[CLIENT_REDACTED]',
                    'city': {'name': '[CLIENT_REDACTED]'},
                    'state': 'CA',
                    'house_no': '[CLIENT_REDACTED]',
                    'zip': ['[CLIENT_REDACTED]', '[CLIENT_REDACTED]']
                }],
                'phone': '[CLIENT_REDACTED]',
                'email': 'jdoe@example.com'
            }");

    JObject resultObject = JObject.Parse(Encoding.UTF8.GetString(result));
    Assert.IsTrue(JToken.DeepEquals(expectedObject, resultObject));

    // Top-level arrays and a malformed path must not skip redaction of the rest
    var arr = Client.RedactFields("[{\"password\":\"hunter2\"}]", new List<string> { "$[*].password", "$[" });
    Assert.That(Encoding.UTF8.GetString(arr), Does.Not.Contain("hunter2"));
  }

  [Test]
  public void TestRedactHeaders()
  {
    // Define test data
    var headers = new Dictionary<string, List<string>>
        {
            { "Content-Type", new List<string> { "application/json" } },
            { "Authorization", new List<string> { "Bearer abcdefg" } },
            { "X-Forwarded-For", new List<string> { "127.0.0.1" } }
        };
    var redactList = new List<string> { "authorization", "x-forwarded-for", "invalid-one" };

    // Test success case
    var expected = new Dictionary<string, List<string>>
        {
            { "Content-Type", new List<string> { "application/json" } },
            { "Authorization", new List<string> { "[CLIENT_REDACTED]" } },
            { "X-Forwarded-For", new List<string> { "[CLIENT_REDACTED]" } }
        };
    var actual = Client.RedactHeaders(headers, redactList);
    Assert.That(actual, Is.EqualTo(expected));

    // Sensitive headers are redacted even with no redact list
    actual = Client.RedactHeaders(headers, null);
    Assert.That(actual["Authorization"], Is.EqualTo(new List<string> { "[CLIENT_REDACTED]" }));
    Assert.That(actual["X-Forwarded-For"], Is.EqualTo(new List<string> { "127.0.0.1" }));
  }

  [TestCase(null)]
  [TestCase("X-Secret")]
  public void TestSetAttributesRedactsHeaders(string userHeader)
  {
    using var span = new Activity("test").Start();
    var reqHeaders = new Dictionary<string, List<string>>
    {
      { "Authorization", new() { "Bearer abc" } }, { "cookie", new() { "sid=1" } },
      { "Proxy-Authorization", new() { "Basic x" } }, { "X-Secret", new() { "s3cret" } }, { "Accept", new() { "*/*" } }
    };
    var respHeaders = new Dictionary<string, List<string>> { { "Set-Cookie", new() { "sid=2" } } };
    var config = new Config { RedactHeaders = userHeader == null ? null : new List<string> { userHeader } };
    Client.SetAttributes(span, "example.com", 200, new(), new(), reqHeaders, respHeaders, "GET", "/", "id", "/", "", "", new(), config, "test");

    foreach (var h in new[] { "request.header.Authorization", "request.header.cookie", "request.header.Proxy-Authorization", "response.header.Set-Cookie" })
      Assert.That(span.GetTagItem($"http.{h}"), Is.EqualTo("[CLIENT_REDACTED]"), h);
    Assert.That(span.GetTagItem("http.request.header.X-Secret"), Is.EqualTo(userHeader == null ? "s3cret" : "[CLIENT_REDACTED]"));
    Assert.That(span.GetTagItem("http.request.header.Accept"), Is.EqualTo("*/*"));
  }

  class ThrowingHandler : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) => throw new HttpRequestException("connection refused");
  }

  [Test]
  public async Task TestObservingHandlerRecordsFailedRequests()
  {
    Activity captured = null;
    using var listener = new ActivityListener
    {
      ShouldListenTo = s => s.Name == "APItoolkit.HTTPInstrumentation",
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
      ActivityStopped = a => captured = a
    };
    ActivitySource.AddActivityListener(listener);
    using var http = new HttpClient(new ObservingHandler(new DefaultHttpContext()) { InnerHandler = new ThrowingHandler() });
    var req = new HttpRequestMessage(HttpMethod.Post, "http://api.example.com/users?x=1") { Content = new StringContent("{\"a\":1}") };
    req.Headers.Add("Authorization", "Bearer abc");

    Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(req));

    Assert.That(captured, Is.Not.Null);
    Assert.That(captured.GetTagItem("http.request.method"), Is.EqualTo("POST"));
    Assert.That(captured.GetTagItem("net.host.name"), Is.EqualTo("api.example.com"));
    Assert.That(captured.GetTagItem("http.route"), Is.EqualTo("/users"));
    Assert.That(captured.GetTagItem("http.request.body"), Is.EqualTo(Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"a\":1}"))));
    Assert.That(captured.GetTagItem("http.request.header.Authorization"), Is.EqualTo("[CLIENT_REDACTED]"));
    Assert.That((string)captured.GetTagItem("apitoolkit.errors"), Does.Contain("connection refused"));
    Assert.That(captured.Status, Is.EqualTo(ActivityStatusCode.Error));
  }

  // [Test]
  public async Task MiddlewareTest_ReturnsNotFoundForRequest()
  {
    var config = new Config
    {
      Debug = true,
    };
    var client = APIToolkit.NewClient(config);
    var host = await new HostBuilder()
        .ConfigureWebHostDefaults(webBuilder =>
        {
          webBuilder
                  .UseTestServer()
                  .Configure(app =>
                  {
                    app.Use(async (context, next) =>
                        {
                          var apiToolkit = new ApiToolkit.Net.APIToolkit(next, client);
                          await apiToolkit.InvokeAsync(context);
                        });
                    // app.Use(async (context, next) =>
                    // {
                    //     var input = await new StreamReader(context.Request.Body).ReadToEndAsync();
                    //     context.Response.ContentType = "application/json";
                    //     await context.Response.WriteAsync(input);
                    // });
                  });
        })
        .StartAsync();

    using var c = host.GetTestClient();
    var body = new { Property1 = "Value1", Property2 = "Value2" };
    var response = await c.PostAsync("/", new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json"));
    Console.WriteLine($"🔥 parent apitoolkit metadata in test {response}");
  }
}

