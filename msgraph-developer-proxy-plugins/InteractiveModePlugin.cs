// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Graph.DeveloperProxy.Abstractions;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Http;

namespace Microsoft.Graph.DeveloperProxy.Plugins;

public class InteractiveModePlugin : BaseProxyPlugin {
    public override string Name => nameof(InteractiveModePlugin);

    public override void Register(IPluginEvents pluginEvents,
                            IProxyContext context,
                            ISet<Regex> urlsToWatch,
                            IConfigurationSection? configSection = null) {
        base.Register(pluginEvents, context, urlsToWatch, configSection);

        pluginEvents.BeforeResponse += OnBeforeResponse;
    }

  private void OnBeforeResponse(object? sender, ProxyResponseArgs e)
  {
    var invalid = true;
    do {
      Console.WriteLine("(p)ass, (c)haos, (t)hrottle, co(n)tinue");
      var key = Console.ReadKey(true);
      switch (key.Key)
      {
        case ConsoleKey.P:
          e.ResponseState.RequestMode = RequestMode.PassThru;
          invalid = false;
          break;
        case ConsoleKey.C:
          e.ResponseState.RequestMode = RequestMode.Random;
          invalid = false;
          break;
        case ConsoleKey.T:
          e.ResponseState.RequestMode = RequestMode.R429;
          invalid = false;
          break;
        case ConsoleKey.N:
          e.ResponseState.RequestMode = RequestMode.Continue;
          invalid = false;
          break;
        default:
          Console.WriteLine("Invalid option");
          break;
      }
    } while (invalid);
  }
}
