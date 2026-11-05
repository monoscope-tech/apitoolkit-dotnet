using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.Routing;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Net.Http.Headers;
using static System.Web.HttpUtility;

namespace ApiToolkit.Net
{
  public class APIToolkit
  {
    private readonly RequestDelegate _next;
    private readonly Client _client;

    public APIToolkit(RequestDelegate next, Client client)
    {
      _next = next;
      _client = client;
    }
    private static readonly ActivitySource RegisteredActivity = new ActivitySource("APItoolkit.HTTPInstrumentation");
    public async Task InvokeAsync(HttpContext context)
    {

      using var span = RegisteredActivity.StartActivity("apitoolkit-http-span");
      if (span == null)
      {
        await _next(context);
        return;
      }
      context.Request.EnableBuffering(); // so we can read the body stream multiple times

      var responseBodyStream = new MemoryStream();
      var originalResponseBodyStream = context.Response.Body;
      context.Response.Body = responseBodyStream;
      Guid uuid = Guid.NewGuid();
      var msg_id = uuid.ToString();
      context.Items["APITOOLKIT_MSG_ID"] = msg_id;
      int statusCode = 0;
      var requestBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
      context.Request.Body.Position = 0; // reset the body stream to the beginning
      try
      {

        await _next(context); // execute the next middleware in the pipeline
      }
      catch (Exception ex)
      {
        statusCode = 500;
        Client.ReportError(context, ex);
        throw;
      }
      finally
      {

        responseBodyStream.Seek(0, SeekOrigin.Begin);
        var responseBody = await new StreamReader(responseBodyStream).ReadToEndAsync();
        responseBodyStream.Seek(0, SeekOrigin.Begin);

        await responseBodyStream.CopyToAsync(originalResponseBodyStream);
        context.Response.Body = originalResponseBodyStream;

        var pathParams = context.GetRouteData().Values
            .Where(v => !string.IsNullOrEmpty(v.Value?.ToString()))
            .ToDictionary(v => v.Key, v => v.Value.ToString());
        var urlPath = "";
        var endpoint = context.GetEndpoint();
        if (endpoint != null)
        {
          var routePattern = (endpoint as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern?.RawText;

          if (routePattern != null)
          {
            urlPath = routePattern;
          }
        }


        var responseHeaders = context.Response.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToList());
        var errors = new List<ATError>();
        if (context.Items.TryGetValue("APITOOLKIT_ERRORS", out var errorListObj) && errorListObj is List<ATError> errorList)
        {
          errors = (List<ATError>)errorListObj;
        }
        if (statusCode == 0)
        {
          statusCode = context.Response.StatusCode;
        }
        var contentType = context.Request.ContentType;
        if (contentType != null && contentType.StartsWith("application/x-www-form-urlencoded"))
        {
          var parsedData = ParseQueryString(requestBody);
          var dictionary = parsedData.AllKeys
                                      .Where(key => key != null)
                                      .ToDictionary(key => key ?? string.Empty, key => parsedData[key]);
          requestBody = JsonConvert.SerializeObject(dictionary);

        }
        else if (contentType != null && contentType.StartsWith("multipart/form-data"))
        {
          try
          {
            var form = await context.Request.ReadFormAsync();
            var formData = form.ToDictionary(x => x.Key, x => x.Value.ToString());
            requestBody = System.Text.Json.JsonSerializer.Serialize(formData);

          }
          catch (Exception) { }
        }

        var host = context.Request.Host.Host;
        var method = context.Request.Method;
        var majorVersion = context.Request.Protocol.Split('.')[0];
        var minorVersion = context.Request.Protocol.Split('.')[1];
        var queryParams = context.Request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        var reqHeaders = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToList());
        var rawUrl = context.Request.GetEncodedPathAndQuery();

        Client.SetAttributes(
          span,
          host,
          statusCode,
          queryParams,
          pathParams,
          reqHeaders,
          responseHeaders,
          method,
          rawUrl,
          msg_id,
          urlPath,
          _client.Config.CaptureRequestBody ? requestBody : "",
          _client.Config.CaptureResponseBody ? responseBody : "",
          errors,
          _client.Config,
          "DotNet");

      }
    }

    public static Client NewClient(Config cfg)
    {
      var client = new Client(cfg);
      if (client.Config.Debug)
      {
        Console.WriteLine("APIToolkit: client initialized successfully");
      }
      return client;
    }
  }


  public class Client(Config config)
  {
    public readonly Config Config = config;


    public ObservingHandler APIToolkitObservingHandler(HttpContext? context = null, ATOptions? options = null)
    {
      return new ObservingHandler(context, options);
    }


    public static void ReportError(HttpContext context, Exception error)
    {
      var atError = BuildError(error);

      if (context.Items.TryGetValue("APITOOLKIT_ERRORS", out var errorListObj) && errorListObj is List<ATError> errorList)
      {
        errorList.Add(atError);
      }
      else
      {
        errorList = new List<ATError> { atError };
        context.Items["APITOOLKIT_ERRORS"] = errorList;
      }
    }
    public static void SetAttributes(
    Activity span,
    string host,
    int statusCode,
    Dictionary<string, string> queryParams,
    Dictionary<string, string?> pathParams,
    Dictionary<string, List<string?>> reqHeaders,
    Dictionary<string, List<string?>> respHeaders,
    string method,
    string rawUrl,
    string msgId,
    string urlPath,
    string reqBody,
    string respBody,
    List<ATError> errors,
    Config config,
    string sdkType,
    string parentId = "")
    {
      try
      {
        span.SetTag("net.host.name", host);
        span.SetTag("apitoolkit.msg_id", msgId);
        span.SetTag("http.route", urlPath);
        span.SetTag("http.target", RedactQueryString(rawUrl, config.RedactHeaders));
        span.SetTag("http.request.method", method);
        span.SetTag("http.response.status_code", statusCode);
        span.SetTag("http.request.query_params", JsonConvert.SerializeObject(RedactQueryParams(queryParams, config.RedactHeaders)));
        span.SetTag("http.request.path_params", JsonConvert.SerializeObject(pathParams));
        span.SetTag("apitoolkit.sdk_type", sdkType);
        span.SetTag("apitoolkit.parent_id", parentId ?? "");
        span.SetTag("http.request.body", Convert.ToBase64String(RedactFields(reqBody, config.RedactRequestBody)));
        span.SetTag("http.response.body", Convert.ToBase64String(RedactFields(respBody, config.RedactResponseBody)));
        span.SetTag("apitoolkit.errors", JsonConvert.SerializeObject(errors));
        span.SetTag("apitoolkit.service_version", config.ServiceVersion);
        span.SetTag("apitoolkit.tags", JsonConvert.SerializeObject(config.Tags ?? new List<string>()));

        foreach (var header in RedactHeaders(reqHeaders, config.RedactHeaders))
        {
          span.SetTag($"http.request.header.{header.Key}", string.Join(",", header.Value));
        }

        foreach (var header in RedactHeaders(respHeaders, config.RedactHeaders))
        {
          span.SetTag($"http.response.header.{header.Key}", string.Join(",", header.Value));
        }
      }
      catch (Exception ex)
      {
        if (config.Debug)
        {
          Console.WriteLine($"Error setting attributes: {ex.Message}");
        }
        span.SetStatus(ActivityStatusCode.Error, ex.Message);
      }
    }

    internal static ATError BuildError(Exception error)
    {
      // Create an instance of ATError
      var atError = new ATError
      {
        When = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        ErrorType = error.GetType().Name,
        Message = error.Message,
        StackTrace = error.StackTrace ?? ""
      };

      // Try to obtain RootErrorType and RootErrorMessage from inner exceptions
      var innerException = error;
      while (innerException.InnerException != null)
      {
        innerException = innerException.InnerException;
      }

      atError.RootErrorType = innerException.GetType().Name;
      atError.RootErrorMessage = innerException.Message;

      return atError;
    }
    public static byte[] RedactFields(string dataStr, List<string> jsonPaths)
    {
      var data = System.Text.Encoding.UTF8.GetBytes(dataStr);
      if (jsonPaths is null || jsonPaths.Count == 0 || !data.Any()) return data;

      JToken json;
      try { json = JToken.Parse(dataStr); }
      catch (Exception) { return data; }
      foreach (var jPath in jsonPaths)
      {
        try { json.SelectTokens(jPath).ToList().ForEach(token => token.Replace("[CLIENT_REDACTED]")); }
        catch (Exception) { } // an invalid path must not skip the remaining ones
      }
      return System.Text.Encoding.UTF8.GetBytes(json.ToString());
    }

    const string Redacted = "[CLIENT_REDACTED]";

    // Always redacted, in addition to Config.RedactHeaders
    public static readonly string[] DefaultRedactHeaders = ["authorization", "cookie", "set-cookie", "proxy-authorization", "x-api-key"];
    public static readonly string[] DefaultRedactQueryParams = ["api_key", "apikey", "access_token", "token", "password", "secret", "client_secret", "signature", "sig", "auth"];

    static HashSet<string> RedactSet(string[] defaults, List<string> redactList) =>
      new(defaults.Concat(redactList ?? []), StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, List<string>> RedactHeaders(Dictionary<string, List<string>> headers, List<string> redactList)
    {
      var redact = RedactSet(DefaultRedactHeaders, redactList);
      return headers.ToDictionary(kvp => kvp.Key, kvp => redact.Contains(kvp.Key) ? [Redacted] : kvp.Value);
    }

    public static Dictionary<string, string> RedactQueryParams(Dictionary<string, string> query, List<string> redactList)
    {
      var redact = RedactSet(DefaultRedactQueryParams, redactList);
      return query?.ToDictionary(kv => kv.Key, kv => redact.Contains(kv.Key) ? Redacted : kv.Value);
    }

    // Rewrites only the matching values, so the rest of the URL stays exactly as sent
    public static string RedactQueryString(string url, List<string> redactList)
    {
      var q = url?.IndexOf('?') ?? -1;
      if (q < 0) return url;
      var redact = RedactSet(DefaultRedactQueryParams, redactList);
      var pairs = url[(q + 1)..].Split('&').Select(p =>
      {
        var eq = p.IndexOf('=');
        return eq >= 0 && redact.Contains(UrlDecode(p[..eq])) ? p[..(eq + 1)] + Uri.EscapeDataString(Redacted) : p;
      });
      return url[..(q + 1)] + string.Join("&", pairs);
    }
  }



  public class Config
  {
    public bool Debug { get; set; }
    public bool VerboseDebug { get; set; }
    public string ServiceVersion { get; set; }
    public string ServiceName { get; set; }
    public bool CaptureRequestBody { get; set; }
    public bool CaptureResponseBody { get; set; }
    public List<string> Tags { get; set; }
    public string? PathWildCard { get; set; }

    public List<string> RedactHeaders { get; set; }
    public List<string> RedactRequestBody { get; set; }
    public List<string> RedactResponseBody { get; set; }
  }

  public class ATError
  {

    [JsonProperty("when")]
    public string When { get; set; }
    [JsonProperty("error_type")]
    public string ErrorType { get; set; }
    [JsonProperty("root_error_type")]
    public string RootErrorType { get; set; }
    [JsonProperty("message")]
    public string Message { get; set; }
    [JsonProperty("root_error_message")]

    public string RootErrorMessage { get; set; }
    [JsonProperty("stack_trace")]
    public string StackTrace { get; set; }
  }


  public class ATOptions
  {
    public string? PathWildCard { get; set; }
    public List<string> RedactHeaders { get; set; }
    public List<string> RedactRequestBody { get; set; }
    public List<string> RedactResponseBody { get; set; }
  }
  public class ObservingHandler : DelegatingHandler
  {
    private readonly HttpContext _context;
    private readonly Config _options;
    private readonly string? _msg_id;
    private static readonly ActivitySource RegisteredActivity = new ActivitySource("APItoolkit.HTTPInstrumentation");

    public ObservingHandler(HttpContext? httpContext = null, ATOptions? options = null) : base(new HttpClientHandler())
    {
      _context = httpContext;
      _options = new Config { RedactHeaders = new List<string> { }, RedactRequestBody = new List<string> { }, RedactResponseBody = new List<string> { } };
      if (options != null)
      {
        _options.RedactHeaders = options.RedactHeaders;
        _options.RedactRequestBody = options.RedactRequestBody;
        _options.RedactResponseBody = options.RedactResponseBody;
        _options.PathWildCard = options.PathWildCard;
      }
      if (httpContext != null)
      {
        if (httpContext.Items.TryGetValue("APITOOLKIT_MSG_ID", out var msg_id) && msg_id != null)
        {
          _msg_id = msg_id.ToString();
        }
      }
    }


    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {

      using var span = RegisteredActivity.StartActivity("apitoolkit-http-span");
      var reqHeaders = AllHeaders(request.Headers, request.Content);
      var reqBody = "";
      if (request != null && request.Content != null)
      {
        reqBody = await request.Content.ReadAsStringAsync(cancellationToken);
      }


      HttpResponseMessage response = null;
      var errors = new List<ATError>();
      try
      {
        return response = await base.SendAsync(request, cancellationToken);
      }
      catch (Exception ex)
      {
        errors.Add(Client.BuildError(ex));
        span?.SetStatus(ActivityStatusCode.Error, ex.Message);
        throw;
      }
      finally
      {
        // Recorded on failures too, so the span keeps the request details
        await RecordSpan(span, request, reqHeaders, reqBody, response, errors);
      }
    }

    private async Task RecordSpan(Activity span, HttpRequestMessage request, Dictionary<string, List<string>> reqHeaders, string reqBody, HttpResponseMessage response, List<ATError> errors)
    {
      if (span == null) return;
      var RequestUri = request.RequestUri;
      try
      {
        var respHeaders = response == null ? new() : AllHeaders(response.Headers, response.Content);
        var respBody = response == null ? "" : await response.Content.ReadAsStringAsync();
        var queryDictCol = ParseQueryString(RequestUri?.Query ?? "");

        var queryDict = new Dictionary<string, string>();
        foreach (string key in queryDictCol)
        {
          queryDict[key] = queryDictCol[key] ?? "";
        }

        Guid uuid = Guid.NewGuid();
        var m_id = uuid.ToString();


        Client.SetAttributes(
          span,
          RequestUri?.Host ?? "",
          (int)(response?.StatusCode ?? 0),
          queryDict,
          ParsePathPattern(_options.PathWildCard ?? RequestUri?.AbsolutePath ?? "", RequestUri?.AbsolutePath ?? ""),
          reqHeaders,
          respHeaders,
          request.Method.ToString(),
          RequestUri?.PathAndQuery ?? "",
          m_id,
          _options.PathWildCard ?? RequestUri?.AbsolutePath ?? "",
          reqBody,
          respBody,
          errors,
          _options,
          "DotNetOutgoing",
           _msg_id ?? ""
          );
      }
      catch (Exception) { }
    }
    // Content-Type, Content-Length and friends live on the content, not the message
    static Dictionary<string, List<string>> AllHeaders(HttpHeaders headers, HttpContent content) =>
      headers.Concat((IEnumerable<KeyValuePair<string, IEnumerable<string>>>)content?.Headers ?? [])
        .GroupBy(h => h.Key).ToDictionary(g => g.Key, g => g.SelectMany(h => h.Value).ToList());

    static Dictionary<string, string?> ParsePathPattern(string pattern, string url)
    {
      var result = new Dictionary<string, string?>();

      var patternParts = pattern.Split('/');
      var urlParts = url.Split('/');

      for (int i = 0; i < patternParts.Length; i++)
      {
        string patternPart = patternParts[i];

        if (patternPart.StartsWith('{') && patternPart.EndsWith('}'))
        {
          string variableName = patternPart.Trim('{', '}');
          string urlPart = "";
          if (i < urlParts.Length)
          {
            urlPart = urlParts[i];
          }
          result[variableName] = urlPart;
        }
      }

      return result;
    }
  }

}
