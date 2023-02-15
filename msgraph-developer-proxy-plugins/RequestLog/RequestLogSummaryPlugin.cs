// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Graph.DeveloperProxy.Abstractions;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Http;

namespace Microsoft.Graph.DeveloperProxy.Plugins.RequestLog;

public class RequestLogSummaryPlugin : BaseProxyPlugin
{
  public override string Name => nameof(RequestLogSummaryPlugin);

  public override void Register(IPluginEvents pluginEvents,
                          IProxyContext context,
                          ISet<Regex> urlsToWatch,
                          IConfigurationSection? configSection = null)
  {
    base.Register(pluginEvents, context, urlsToWatch, configSection);

    pluginEvents.AfterRecordingStop += AfterRecordingStop;
  }

  private void AfterRecordingStop(object? sender, RecordingArgs e)
  {
    if (e.RequestLogs is null || e.RequestLogs.Count() == 0)
    {
      return;
    }

    foreach (var requestLog in e.RequestLogs)
    {
      Console.WriteLine(String.Join(" ", requestLog.Message));
    }
  }
}
