// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Core.Diagnostics;
using Azure.Identity;
using Microsoft.DevProxy.Abstractions;
using Microsoft.DevProxy.Plugins.RequestLogs.ApiCenter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace Microsoft.DevProxy.Plugins.RequestLogs;

internal class ApiCenterOnboardingPluginConfiguration
{
    public string SubscriptionId { get; set; } = "";
    public string ResourceGroupName { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string WorkspaceName { get; set; } = "default";
    public bool CreateApicEntryForNewApis { get; set; } = true;
    public bool ExcludeDevCredentials { get; set; } = false;
    public bool ExcludeProdCredentials { get; set; } = true;
}

public class ApiCenterOnboardingPlugin : BaseProxyPlugin
{
    private ApiCenterOnboardingPluginConfiguration _configuration = new();
    private readonly string[] _scopes = ["https://management.azure.com/.default"];
    private TokenCredential _credential = new DefaultAzureCredential();
    private HttpClient? _httpClient;
    private JsonSerializerOptions _jsonSerializerOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ApiCenterOnboardingPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : base(pluginEvents, context, logger, urlsToWatch, configSection)
    {
    }

    public override string Name => nameof(ApiCenterOnboardingPlugin);

    public override void Register()
    {
        base.Register();

        ConfigSection?.Bind(_configuration);

        if (string.IsNullOrEmpty(_configuration.SubscriptionId))
        {
            Logger.LogError("Specify SubscriptionId in the {plugin} configuration. The {plugin} will not be used.", Name, Name);
            return;
        }
        if (string.IsNullOrEmpty(_configuration.ResourceGroupName))
        {
            Logger.LogError("Specify ResourceGroupName in the {plugin} configuration. The {plugin} will not be used.", Name, Name);
            return;
        }
        if (string.IsNullOrEmpty(_configuration.ServiceName))
        {
            Logger.LogError("Specify ServiceName in the {plugin} configuration. The {plugin} will not be used.", Name, Name);
            return;
        }
        if (_configuration.ExcludeDevCredentials && _configuration.ExcludeProdCredentials)
        {
            Logger.LogError("Both ExcludeDevCredentials and ExcludeProdCredentials are set to true. You need to use at least one set of credentials The {plugin} will not be used.", Name);
            return;
        }

        var credentials = new List<TokenCredential>();
        if (!_configuration.ExcludeDevCredentials)
        {
            credentials.AddRange([
                new SharedTokenCacheCredential(),
                new VisualStudioCredential(),
                new VisualStudioCodeCredential(),
                new AzureCliCredential(),
                new AzurePowerShellCredential(),
                new AzureDeveloperCliCredential(),
            ]);
        }
        if (!_configuration.ExcludeProdCredentials)
        {
            credentials.AddRange([
                new EnvironmentCredential(),
                new WorkloadIdentityCredential(),
                new ManagedIdentityCredential()
            ]);
        }
        _credential = new ChainedTokenCredential(credentials.ToArray());

        if (Logger.IsEnabled(LogLevel.Debug) == true)
        {
            var consoleListener = AzureEventSourceListener.CreateConsoleLogger(EventLevel.Verbose);
        }

        Logger.LogDebug("[{now}] Plugin {plugin} checking Azure auth...", DateTime.Now, Name);
        try
        {
            _ = _credential.GetTokenAsync(new TokenRequestContext(_scopes), CancellationToken.None).Result;
        }
        catch (AuthenticationFailedException ex)
        {
            Logger.LogError(ex, "Failed to authenticate with Azure. The {plugin} will not be used.", Name);
            return;
        }
        Logger.LogDebug("[{now}] Plugin {plugin} auth confirmed...", DateTime.Now, Name);

        var authenticationHandler = new AuthenticationDelegatingHandler(_credential, _scopes)
        {
            InnerHandler = new HttpClientHandler()
        };
        _httpClient = new HttpClient(authenticationHandler);

        PluginEvents.AfterRecordingStop += AfterRecordingStop;
    }

    private async Task AfterRecordingStop(object sender, RecordingArgs e)
    {
        if (!e.RequestLogs.Any())
        {
            Logger.LogDebug("No requests to process");
            return;
        }

        Logger.LogInformation("Checking if recorded API requests belong to APIs in API Center...");

        Debug.Assert(_httpClient is not null);

        var apis = await LoadApisFromApiCenter();
        if (apis == null || !apis.Value.Any())
        {
            Logger.LogInformation("No APIs found in API Center");
            return;
        }

        var apiDefinitions = await LoadApiDefinitions(apis.Value);

        var newApis = new List<Tuple<string, string>>();
        var interceptedRequests = e.RequestLogs
            .Where(l => l.MessageType == MessageType.InterceptedRequest)
            .Select(request =>
            {
                var methodAndUrl = request.MessageLines.First().Split(' ');
                return new Tuple<string, string>(methodAndUrl[0], methodAndUrl[1]);
            })
            .Distinct();
        foreach (var request in interceptedRequests)
        {
            Logger.LogDebug("Processing request {method} {url}...", request.Item1, request.Item2);

            var requestMethod = request.Item1;
            var requestUrl = request.Item2;

            var apiDefinition = apiDefinitions.FirstOrDefault(x => requestUrl.Contains(x.Key)).Value;
            if (apiDefinition.Id is null)
            {
                Logger.LogDebug("No matching API definition not found for {requestUrl}. Adding new API...", requestUrl);
                newApis.Add(new(requestMethod, requestUrl));
                continue;
            }

            await EnsureApiDefinition(apiDefinition);

            if (apiDefinition.Definition is null)
            {
                Logger.LogDebug("API definition not found for {requestUrl} so nothing to compare to. Adding new API...", requestUrl);
                newApis.Add(new(requestMethod, requestUrl));
                continue;
            }

            var pathItem = FindMatchingPathItem(requestUrl, apiDefinition.Definition);
            if (pathItem is null)
            {
                Logger.LogDebug("No matching path found for {requestUrl}. Adding new API...", requestUrl);
                newApis.Add(new(requestMethod, requestUrl));
                continue;
            }

            var operation = pathItem.Operations.FirstOrDefault(x => x.Key.ToString().Equals(requestMethod, StringComparison.OrdinalIgnoreCase)).Value;
            if (operation is null)
            {
                Logger.LogDebug("No matching operation found for {requestMethod} {requestUrl}. Adding new API...", requestMethod, requestUrl);

                newApis.Add(new(requestMethod, requestUrl));
                continue;
            }
        }

        if (!newApis.Any())
        {
            Logger.LogInformation("No new APIs found");
            return;
        }

        // dedupe newApis
        newApis = newApis.Distinct().ToList();

        var apisPerHost = newApis.GroupBy(x => new Uri(x.Item2).Host);

        var newApisMessageChunks = new List<string>(["New APIs that aren't registered in Azure API Center:", ""]);
        foreach (var apiPerHost in apisPerHost)
        {
            newApisMessageChunks.Add($"{apiPerHost.Key}:");
            newApisMessageChunks.AddRange(apiPerHost.Select(a => $"  {a.Item1} {a.Item2}"));
        }

        Logger.LogInformation(string.Join(Environment.NewLine, newApisMessageChunks));

        if (!_configuration.CreateApicEntryForNewApis)
        {
            return;
        }

        await CreateApisInApiCenter(apisPerHost);
    }

    async Task CreateApisInApiCenter(IEnumerable<IGrouping<string, Tuple<string, string>>> apisPerHost)
    {
        Debug.Assert(_httpClient is not null);

        Logger.LogInformation("{newLine}Creating new API entries in API Center...", Environment.NewLine);

        foreach (var apiPerHost in apisPerHost)
        {
            var host = apiPerHost.Key;
            // trim to 50 chars which is max length for API name
            var apiName = MaxLength($"new-{host.Replace(".", "-")}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", 50);
            Logger.LogInformation("  Creating API {apiName} for {host}...", apiName, host);

            var title = $"New APIs: {host}";
            var description = new List<string>(["New APIs discovered by Dev Proxy", ""]);
            description.AddRange(apiPerHost.Select(a => $"  {a.Item1} {a.Item2}").ToArray());
            var payload = new
            {
                properties = new
                {
                    title,
                    description = string.Join(Environment.NewLine, description),
                    kind = "REST",
                    type = "rest"
                }
            };
            var content = new StringContent(JsonSerializer.Serialize(payload, _jsonSerializerOptions), Encoding.UTF8, "application/json");
            var createRes = await _httpClient.PutAsync($"https://management.azure.com/subscriptions/{_configuration.SubscriptionId}/resourceGroups/{_configuration.ResourceGroupName}/providers/Microsoft.ApiCenter/services/{_configuration.ServiceName}/workspaces/{_configuration.WorkspaceName}/apis/{apiName}?api-version=2024-03-01", content);
            if (createRes.IsSuccessStatusCode)
            {
                Logger.LogDebug("API created successfully");
            }
            else
            {
                Logger.LogError("Failed to create API {apiName} for {host}", apiName, host);
            }
            var createResContent = await createRes.Content.ReadAsStringAsync();
            Logger.LogDebug(createResContent);
        }

        Logger.LogInformation("DONE");
    }

    async Task<Collection<Api>?> LoadApisFromApiCenter()
    {
        Debug.Assert(_httpClient is not null);

        Logger.LogInformation("Loading APIs from API Center...");

        var res = await _httpClient.GetStringAsync($"https://management.azure.com/subscriptions/{_configuration.SubscriptionId}/resourceGroups/{_configuration.ResourceGroupName}/providers/Microsoft.ApiCenter/services/{_configuration.ServiceName}/workspaces/{_configuration.WorkspaceName}/apis?api-version=2024-03-01");
        return JsonSerializer.Deserialize<Collection<Api>>(res, _jsonSerializerOptions);
    }

    OpenApiPathItem? FindMatchingPathItem(string requestUrl, OpenApiDocument openApiDocument)
    {
        foreach (var path in openApiDocument.Paths)
        {
            var urlPath = path.Key;
            Logger.LogDebug("Checking path {urlPath}...", urlPath);

            // check if path contains parameters. If it does,
            // replace them with regex
            if (urlPath.Contains('{'))
            {
                Logger.LogDebug("Path {urlPath} contains parameters and will be converted to Regex", urlPath);

                foreach (var parameter in path.Value.Parameters)
                {
                    urlPath = urlPath.Replace($"{{{parameter.Name}}}", $"([^/]+)");
                }

                Logger.LogDebug("Converted path to Regex: {urlPath}", urlPath);
                var regex = new Regex(urlPath);
                if (regex.IsMatch(requestUrl))
                {
                    Logger.LogDebug("Regex matches {requestUrl}", requestUrl);

                    return path.Value;
                }

                Logger.LogDebug("Regex does not match {requestUrl}", requestUrl);
            }
            else
            {
                if (requestUrl.Contains(urlPath, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogDebug("{requestUrl} contains {urlPath}", requestUrl, urlPath);

                    return path.Value;
                }

                Logger.LogDebug("{requestUrl} doesn't contain {urlPath}", requestUrl, urlPath);
            }
        }

        return null;
    }

    async Task<Dictionary<string, ApiDefinition>> LoadApiDefinitions(Api[] apis)
    {
        Logger.LogInformation("Loading API definitions from API Center...");

        var apiDefinitions = new Dictionary<string, ApiDefinition>();

        foreach (var api in apis)
        {
            Debug.Assert(api.Name is not null);

            var apiName = api.Name;
            Logger.LogDebug("Loading API definitions for {apiName}...", apiName);

            var deployments = await LoadApiDeployments(apiName);
            if (deployments == null || !deployments.Value.Any())
            {
                Logger.LogDebug("No deployments found for API {apiName}", apiName);
                continue;
            }

            foreach (var deployment in deployments.Value)
            {
                Debug.Assert(deployment?.Properties?.Server is not null);
                Debug.Assert(deployment?.Properties?.DefinitionId is not null);

                if (!deployment.Properties.Server.RuntimeUri.Any())
                {
                    Logger.LogDebug("No runtime URIs found for deployment {deploymentName}", deployment.Name);
                    continue;
                }

                foreach (var runtimeUri in deployment.Properties.Server.RuntimeUri)
                {
                    apiDefinitions.Add(runtimeUri, new ApiDefinition
                    {
                        Id = deployment.Properties.DefinitionId
                    });
                }
            }
        }

        return apiDefinitions;
    }

    async Task<Collection<ApiDeployment>?> LoadApiDeployments(string apiName)
    {
        Debug.Assert(_httpClient is not null);

        Logger.LogDebug("Loading API deployments for {apiName}...", apiName);

        var res = await _httpClient.GetStringAsync($"https://management.azure.com/subscriptions/{_configuration.SubscriptionId}/resourceGroups/{_configuration.ResourceGroupName}/providers/Microsoft.ApiCenter/services/{_configuration.ServiceName}/workspaces/{_configuration.WorkspaceName}/apis/{apiName}/deployments?api-version=2024-03-01");
        return JsonSerializer.Deserialize<Collection<ApiDeployment>>(res, _jsonSerializerOptions);
    }

    async Task EnsureApiDefinition(ApiDefinition apiDefinition)
    {
        Debug.Assert(_httpClient is not null);

        if (apiDefinition.Properties is not null)
        {
            Logger.LogDebug("API definition already loaded for {apiDefinitionId}", apiDefinition.Id);
            return;
        }

        Logger.LogDebug("Loading API definition for {apiDefinitionId}...", apiDefinition.Id);

        var res = await _httpClient.GetStringAsync($"https://management.azure.com/subscriptions/{_configuration.SubscriptionId}/resourceGroups/{_configuration.ResourceGroupName}/providers/Microsoft.ApiCenter/services/{_configuration.ServiceName}{apiDefinition.Id}?api-version=2024-03-01");
        var definition = JsonSerializer.Deserialize<ApiDefinition>(res, _jsonSerializerOptions);
        if (definition is null)
        {
            Logger.LogError("Failed to deserialize API definition for {apiDefinitionId}", apiDefinition.Id);
            return;
        }

        apiDefinition.Properties = definition.Properties;
        if (apiDefinition.Properties?.Specification?.Name != "openapi")
        {
            Logger.LogDebug("API definition is not OpenAPI for {apiDefinitionId}", apiDefinition.Id);
            return;
        }

        var definitionRes = await _httpClient.PostAsync($"https://management.azure.com/subscriptions/{_configuration.SubscriptionId}/resourceGroups/{_configuration.ResourceGroupName}/providers/Microsoft.ApiCenter/services/{_configuration.ServiceName}{apiDefinition.Id}/exportSpecification?api-version=2024-03-01", null);
        var exportResult = await definitionRes.Content.ReadFromJsonAsync<ApiSpecExportResult>();
        if (exportResult is null)
        {
            Logger.LogError("Failed to deserialize exported API definition for {apiDefinitionId}", apiDefinition.Id);
            return;
        }

        if (exportResult.Format != ApiSpecExportResultFormat.Inline)
        {
            Logger.LogDebug("API definition is not inline for {apiDefinitionId}", apiDefinition.Id);
            return;
        }

        try
        {
            apiDefinition.Definition = new OpenApiStringReader().Read(exportResult.Value, out var diagnostic);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse OpenAPI document for {apiDefinitionId}", apiDefinition.Id);
            return;
        }
    }

    private string MaxLength(string input, int maxLength)
    {
        return input.Length <= maxLength ? input : input.Substring(0, maxLength);
    }
}
