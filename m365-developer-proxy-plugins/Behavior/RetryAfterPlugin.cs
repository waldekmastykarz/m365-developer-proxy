// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft365.DeveloperProxy.Abstractions;
using Microsoft365.DeveloperProxy.Plugins.RandomErrors;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Microsoft365.DeveloperProxy.Plugins.Behavior;

public class RetryAfterPlugin : BaseProxyPlugin
{
  private readonly Random _random = new();

  public override string Name => nameof(RetryAfterPlugin);

  public override void Register(IPluginEvents pluginEvents,
                       IProxyContext context,
                       ISet<UrlToWatch> urlsToWatch,
                       IConfigurationSection? configSection = null)
  {
    base.Register(pluginEvents, context, urlsToWatch, configSection);

    pluginEvents.BeforeRequest += OnRequest;
  }

  private async Task OnRequest(object? sender, ProxyRequestArgs e)
  {
    if (e.ResponseState.HasBeenSet ||
        _urlsToWatch is null ||
        !e.ShouldExecute(_urlsToWatch))
    {
      return;
    }

    ThrottleIfNecessary(e);
  }

  private void ThrottleIfNecessary(ProxyRequestArgs ev)
  {
    var request = ev.Session.HttpClient.Request;
    var expiredThrottlers = ev.ThrottledRequests.Where(t => t.ResetTime < DateTime.Now);
    foreach (var throttler in expiredThrottlers)
    {
      ev.ThrottledRequests.Remove(throttler);
    }

    if (!ev.ThrottledRequests.Any())
    {
      return;
    }

    if (!ProxyUtils.IsGraphBatchUrl(request.RequestUri)) {
      foreach (var throttler in ev.ThrottledRequests) {
        var throttleInfo = throttler.ShouldThrottle(request.RequestUri, throttler.ThrottlingKey);
        if (throttleInfo.ThrottleForSeconds > 0)
        {
          _logger?.LogRequest(new[] { $"Calling {request.Url} before waiting for the Retry-After period.", "Request will be throttled", $"Throttling on {throttler.ThrottlingKey}" }, MessageType.Failed, new LoggingContext(ev.Session));

          throttler.ResetTime = DateTime.Now.AddSeconds(throttleInfo.ThrottleForSeconds);
          UpdateProxyResponse(ev, throttleInfo);
          return;
        }
      }
    }

    GraphBatchRequestPayload? batchRequest = null;

    try {
      batchRequest = JsonSerializer.Deserialize<GraphBatchRequestPayload>(request.BodyString);
      if (batchRequest is null) {
        return;
      }
    }
    catch {
      return;
    }

    foreach (var throttler in ev.ThrottledRequests) {
      foreach (var requestFromBatch in batchRequest.Requests) {
        var absoluteUrl = ProxyUtils.GetAbsoluteRequestUrlFromBatch(request.RequestUri, requestFromBatch.Url);
        var throttleInfo = throttler.ShouldThrottle(absoluteUrl, throttler.ThrottlingKey);

        if (throttleInfo.ThrottleForSeconds == 0) {
          continue;
        }

        _logger?.LogRequest(new[] { $"Calling {absoluteUrl} before waiting for the Retry-After period.", "Request will be throttled", $"Throttling on {throttler.ThrottlingKey}" }, MessageType.Failed, new LoggingContext(ev.Session));

        throttler.ResetTime = DateTime.Now.AddSeconds(throttleInfo.ThrottleForSeconds);

        var batchResponses = batchRequest.Requests.Select(requestFromBatch2 => {
          // force-throttle the URL, random error for all other requests in the batch
          if (requestFromBatch2.Id == requestFromBatch.Id) {
            return new GraphBatchResponsePayloadResponse {
              Id = requestFromBatch2.Id,
              Status = (int)HttpStatusCode.TooManyRequests, // 429
              Headers = new Dictionary<string, string> {
                { "Retry-After", throttleInfo.ThrottleForSeconds.ToString() }
              }
            };
          }
          
          // pick a random error response for the current request method
          var methodStatusCodes = GraphUtils.MethodStatusCode[ev.Session.HttpClient.Request.Method];
          var errorStatus = methodStatusCodes[_random.Next(0, methodStatusCodes.Length)];
          var headers = new Dictionary<string, string>();

          if (errorStatus == HttpStatusCode.TooManyRequests) {
            var retryAfterInSeconds = 5;
            var retryAfterDate = DateTime.Now.AddSeconds(retryAfterInSeconds);
            var absoluteUrl = ProxyUtils.GetAbsoluteRequestUrlFromBatch(request.RequestUri, requestFromBatch2.Url);
            ev.ThrottledRequests.Add(new ThrottlerInfo(GraphUtils.BuildThrottleKey(absoluteUrl), GraphRandomErrorPlugin.ShouldThrottle, retryAfterDate));
            headers.Add("Retry-After", retryAfterInSeconds.ToString());
          }

          return new GraphBatchResponsePayloadResponse {
            Id = requestFromBatch2.Id,
            Status = (int)errorStatus,
            Headers = headers
          };
        });

        var batchResponse = new GraphBatchResponsePayload {
          Responses = batchResponses.ToArray()
        };
        UpdateProxyBatchResponse(ev, batchResponse);
      }
    }
  }

  private void UpdateProxyResponse(ProxyRequestArgs e, ThrottlingInfo throttlingInfo)
  {
    var headers = new List<HttpHeader>();
    var body = string.Empty;
    var request = e.Session.HttpClient.Request;

    // override the response body and headers for the error response
    if (ProxyUtils.IsGraphRequest(request))
    {
      string requestId = Guid.NewGuid().ToString();
      string requestDate = DateTime.Now.ToString();
      headers.AddRange(ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate));

      body = JsonSerializer.Serialize(new GraphErrorResponseBody(
          new GraphErrorResponseError
          {
            Code = new Regex("([A-Z])").Replace(HttpStatusCode.TooManyRequests.ToString(), m => { return $" {m.Groups[1]}"; }).Trim(),
            Message = BuildApiErrorMessage(request),
            InnerError = new GraphErrorResponseInnerError
            {
              RequestId = requestId,
              Date = requestDate
            }
          })
      );
    }

    headers.Add(new HttpHeader(throttlingInfo.RetryAfterHeaderName, throttlingInfo.ThrottleForSeconds.ToString()));

    e.Session.GenericResponse(body ?? string.Empty, HttpStatusCode.TooManyRequests, headers);
    e.ResponseState.HasBeenSet = true;
  }

  private void UpdateProxyBatchResponse(ProxyRequestArgs ev, GraphBatchResponsePayload response) {
      // failed batch uses a fixed 424 error status code
      var errorStatus = HttpStatusCode.FailedDependency;

      SessionEventArgs session = ev.Session;
      string requestId = Guid.NewGuid().ToString();
      string requestDate = DateTime.Now.ToString();
      Request request = session.HttpClient.Request;
      var headers = ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate);

      var options = new JsonSerializerOptions {
          DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
      };
      string body = JsonSerializer.Serialize(response, options);
      _logger?.LogRequest(new[] { $"{(int)errorStatus} {errorStatus.ToString()}" }, MessageType.Chaos, new LoggingContext(ev.Session));
      session.GenericResponse(body, errorStatus, headers);
  }

  private static string BuildApiErrorMessage(Request r) => $"Some error was generated by the proxy. {(ProxyUtils.IsGraphRequest(r) ? ProxyUtils.IsSdkRequest(r) ? "" : String.Join(' ', MessageUtils.BuildUseSdkForErrorsMessage(r)) : "")}";
}
