// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;

namespace Microsoft365.DeveloperProxy.Plugins;

public class GraphUtils
{
  // throttle requests per workload
  public static string BuildThrottleKey(Uri requestUri)
  {
    if (requestUri.Segments.Length < 3)
    {
      return requestUri.Host;
    }

    // first segment is /
    // second segment is Graph version (v1.0, beta)
    // third segment is the workload (users, groups, etc.)
    // segment can end with / if there are other segments following
    var workload = requestUri.Segments[2].Trim('/');

    // TODO: handle 'me' which is a proxy to other resources

    return workload;
  }

  public static readonly Dictionary<string, HttpStatusCode[]> MethodStatusCode = new()
  {
    {
      "GET", new[] {
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout
      }
    },
    {
      "POST", new[] {
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
        HttpStatusCode.InsufficientStorage
      }
    },
    {
      "PUT", new[] {
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
        HttpStatusCode.InsufficientStorage
      }
    },
    {
      "PATCH", new[] {
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout
      }
    },
    {
      "DELETE", new[] {
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
        HttpStatusCode.InsufficientStorage
      }
    }
  };
}