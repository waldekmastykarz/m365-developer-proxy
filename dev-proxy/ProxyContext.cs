// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.using Microsoft.Extensions.Configuration;

using System.Security.Cryptography.X509Certificates;
using Microsoft.DevProxy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Microsoft.DevProxy;

internal class ProxyContext : IProxyContext
{
    public ILogger Logger { get; }
    public IProxyConfiguration Configuration { get; }
    public X509Certificate2? Certificate { get; }

    public ProxyContext(ILogger logger, IProxyConfiguration configuration, X509Certificate2? certificate)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Certificate = certificate;
    }
}
