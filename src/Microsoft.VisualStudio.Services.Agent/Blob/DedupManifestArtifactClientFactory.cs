// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Services.BlobStore.WebApi;
using Microsoft.VisualStudio.Services.Content.Common.Tracing;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.Content.Common;
using Microsoft.VisualStudio.Services.BlobStore.Common.Telemetry;
using Agent.Sdk;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.BlobStore.Common;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.VisualStudio.Services.BlobStore.WebApi.Contracts;
using Agent.Sdk.Knob;

namespace Microsoft.VisualStudio.Services.Agent.Blob
{
    [ServiceLocator(Default = typeof(DedupManifestArtifactClientFactory))]
    public interface IDedupManifestArtifactClientFactory
    {
        /// <summary>
        /// Creates a DedupManifestArtifactClient client.
        /// </summary>
        /// <param name="verbose">If true emit verbose telemetry.</param>
        /// <param name="traceOutput">Action used for logging.</param>
        /// <param name="connection">VssConnection</param>
        /// <param name="maxParallelism">Maximum number of parallel threads that should be used for download. If 0 then 
        /// use the system default. </param>
        /// <param name="cancellationToken">Cancellation token used for both creating clients and verifying client conneciton.</param>
        /// <returns>Tuple of the client and the telemtery client</returns>
        Task<(DedupManifestArtifactClient client, BlobStoreClientTelemetry telemetry)> CreateDedupManifestClientAsync(
            bool verbose,
            Action<string> traceOutput,
            VssConnection connection,
            int maxParallelism,
            IDomainId domainId,
            BlobStore.WebApi.Contracts.Client client,
            AgentTaskPluginExecutionContext context,
            CancellationToken cancellationToken);

        /// <summary>
        /// Creates a DedupStoreClient client.
        /// </summary>
        /// <param name="verbose">If true emit verbose telemetry.</param>
        /// <param name="traceOutput">Action used for logging.</param>
        /// <param name="connection">VssConnection</param>
        /// <param name="maxParallelism">Maximum number of parallel threads that should be used for download. If 0 then 
        /// use the system default. </param>
        /// <param name="cancellationToken">Cancellation token used for both creating clients and verifying client conneciton.</param>
        /// <returns>Tuple of the client and the telemtery client</returns>
        Task<(DedupStoreClient client, BlobStoreClientTelemetryTfs telemetry)> CreateDedupClientAsync(
            bool verbose,
            Action<string> traceOutput,
            VssConnection connection,
            int maxParallelism,
            CancellationToken cancellationToken);

        /// <summary>
        /// Gets the maximum parallelism to use for dedup related downloads and uploads.
        /// </summary>
        /// <param name="context">Context which may specify overrides for max parallelism</param>
        /// <returns>max parallelism</returns>
        int GetDedupStoreClientMaxParallelism(AgentTaskPluginExecutionContext context);
    }

    public class DedupManifestArtifactClientFactory : IDedupManifestArtifactClientFactory
    {
        // Old default for hosted agents was 16*2 cores = 32. 
        // In my tests of a node_modules folder, this 32x parallelism was consistently around 47 seconds.
        // At 192x it was around 16 seconds and 256x was no faster.
        private const int DefaultDedupStoreClientMaxParallelism = 192;

        private HashType? HashType { get; set; }

        public static readonly DedupManifestArtifactClientFactory Instance = new();

        private DedupManifestArtifactClientFactory()
        {
        }

        public async Task<(DedupManifestArtifactClient client, BlobStoreClientTelemetry telemetry)> CreateDedupManifestClientAsync(
            bool verbose,
            Action<string> traceOutput,
            VssConnection connection,
            int maxParallelism,
            IDomainId domainId,
            BlobStore.WebApi.Contracts.Client client,
            AgentTaskPluginExecutionContext context,
            CancellationToken cancellationToken)
        {
            const int maxRetries = 5;
            var tracer = CreateArtifactsTracer(verbose, traceOutput);
            if (maxParallelism == 0)
            {
                maxParallelism = DefaultDedupStoreClientMaxParallelism;
            }
            traceOutput($"Max dedup parallelism: {maxParallelism}");

            IDedupStoreHttpClient dedupStoreHttpClient = await AsyncHttpRetryHelper.InvokeAsync(
                async () =>
                {
                    ArtifactHttpClientFactory factory = new ArtifactHttpClientFactory(
                        connection.Credentials,
                        connection.Settings.SendTimeout,
                        tracer,
                        cancellationToken);

                    IDedupStoreHttpClient dedupHttpclient;
                    // this is actually a hidden network call to the location service:
                    if (domainId == WellKnownDomainIds.DefaultDomainId)
                    {
                        dedupHttpclient = factory.CreateVssHttpClient<IDedupStoreHttpClient, DedupStoreHttpClient>(connection.GetClient<DedupStoreHttpClient>().BaseAddress);
                    }
                    else
                    {
                        IDomainDedupStoreHttpClient domainClient = factory.CreateVssHttpClient<IDomainDedupStoreHttpClient, DomainDedupStoreHttpClient>(connection.GetClient<DomainDedupStoreHttpClient>().BaseAddress);
                        dedupHttpclient = new DomainHttpClientWrapper(domainId, domainClient);
                    }

                    this.HashType ??= await GetClientHashTypeAsync(factory, connection, client, tracer, context, cancellationToken);

                    return dedupHttpclient;
                },
                maxRetries: maxRetries,
                tracer: tracer,
                canRetryDelegate: e => true,
                context: nameof(CreateDedupManifestClientAsync),
                cancellationToken: cancellationToken,
                continueOnCapturedContext: false);

            var telemetry = new BlobStoreClientTelemetry(tracer, dedupStoreHttpClient.BaseAddress);
            traceOutput($"Hashtype: {this.HashType.Value}");

            if (this.HashType == BuildXL.Cache.ContentStore.Hashing.HashType.Dedup1024K)
            {
                dedupStoreHttpClient.RecommendedChunkCountPerCall = 10; // This is to workaround IIS limit - https://learn.microsoft.com/en-us/iis/configuration/system.webserver/security/requestfiltering/requestlimits/
            }

            var dedupClient = new DedupStoreClientWithDataport(dedupStoreHttpClient, new DedupStoreClientContext(maxParallelism), this.HashType.Value); 
            return (new DedupManifestArtifactClient(telemetry, dedupClient, tracer), telemetry);
        }

        public async Task<(DedupStoreClient client, BlobStoreClientTelemetryTfs telemetry)> CreateDedupClientAsync(
            bool verbose,
            Action<string> traceOutput,
            VssConnection connection,
            int maxParallelism,
            CancellationToken cancellationToken)
        {
            const int maxRetries = 5;
            var tracer = CreateArtifactsTracer(verbose, traceOutput);
            if (maxParallelism == 0)
            {
                maxParallelism = DefaultDedupStoreClientMaxParallelism;
            }
            traceOutput($"Max dedup parallelism: {maxParallelism}");
            var dedupStoreHttpClient = await AsyncHttpRetryHelper.InvokeAsync(
                () =>
                {
                    ArtifactHttpClientFactory factory = new ArtifactHttpClientFactory(
                        connection.Credentials,
                        connection.Settings.SendTimeout, // copy timeout settings from connection provided by agent
                        tracer,
                        cancellationToken);

                    // this is actually a hidden network call to the location service:
                    return Task.FromResult(factory.CreateVssHttpClient<IDedupStoreHttpClient, DedupStoreHttpClient>(connection.GetClient<DedupStoreHttpClient>().BaseAddress));
                },
                maxRetries: maxRetries,
                tracer: tracer,
                canRetryDelegate: e => true,
                context: nameof(CreateDedupManifestClientAsync),
                cancellationToken: cancellationToken,
                continueOnCapturedContext: false);

            var telemetry = new BlobStoreClientTelemetryTfs(tracer, dedupStoreHttpClient.BaseAddress, connection);
            var client = new DedupStoreClient(dedupStoreHttpClient, maxParallelism);
            return (client, telemetry);
        }

        public int GetDedupStoreClientMaxParallelism(AgentTaskPluginExecutionContext context)
        {
            ConfigureEnvironmentVariables(context);

            int parallelism = DefaultDedupStoreClientMaxParallelism;

            if (context.Variables.TryGetValue("AZURE_PIPELINES_DEDUP_PARALLELISM", out VariableValue v))
            {
                if (!int.TryParse(v.Value, out parallelism))
                {
                    context.Output($"Could not parse the value of AZURE_PIPELINES_DEDUP_PARALLELISM, '{v.Value}', as an integer. Defaulting to {DefaultDedupStoreClientMaxParallelism}");
                    parallelism = DefaultDedupStoreClientMaxParallelism;
                }
                else
                {
                    context.Output($"Overriding default max parallelism with {parallelism}");
                }
            }
            else
            {
                context.Output($"Using default max parallelism.");
            }

            return parallelism;
        }

        private static readonly string[] EnvironmentVariables = new[] { "VSO_DEDUP_REDIRECT_TIMEOUT_IN_SEC" };

        private static void ConfigureEnvironmentVariables(AgentTaskPluginExecutionContext context)
        {
            foreach (string varName in EnvironmentVariables)
            {
                if (context.Variables.TryGetValue(varName, out VariableValue v))
                {
                    if (v.Value.Equals(Environment.GetEnvironmentVariable(varName), StringComparison.Ordinal))
                    {
                        context.Output($"{varName} is already set to `{v.Value}`.");
                    }
                    else
                    {
                        Environment.SetEnvironmentVariable(varName, v.Value);
                        context.Output($"Set {varName} to `{v.Value}`.");
                    }
                }
            }
        }


        public static IAppTraceSource CreateArtifactsTracer(bool verbose, Action<string> traceOutput)
        {
            return new CallbackAppTraceSource(
                str => traceOutput(str),
                verbose
                    ? System.Diagnostics.SourceLevels.Verbose
                    : System.Diagnostics.SourceLevels.Information,
                includeSeverityLevel: verbose);
        }

        private static async Task<HashType> GetClientHashTypeAsync(
            ArtifactHttpClientFactory factory,
            VssConnection connection,
            BlobStore.WebApi.Contracts.Client client,
            IAppTraceSource tracer,
            AgentTaskPluginExecutionContext context,
            CancellationToken cancellationToken)
        {
            HashType hashType = ChunkerHelper.DefaultChunkHashType;

            if (AgentKnobs.AgentEnablePipelineArtifactLargeChunkSize.GetValue(context).AsBoolean())
            {
                try
                {
                    var blobUri = connection.GetClient<ClientSettingsHttpClient>().BaseAddress;
                    var clientSettingsHttpClient = factory.CreateVssHttpClient<IClientSettingsHttpClient, ClientSettingsHttpClient>(blobUri);
                    ClientSettingsInfo clientSettings = await clientSettingsHttpClient.GetSettingsAsync(client, cancellationToken);
                    if (clientSettings != null && clientSettings.Properties.ContainsKey(ClientSettingsConstants.ChunkSize))
                    {
                        HashTypeExtensions.Deserialize(clientSettings.Properties[ClientSettingsConstants.ChunkSize], out hashType);
                    }
                }
                catch (Exception exception)
                {
                    tracer.Warn($"Error while retrieving hash type for {client}. Exception: {exception}");
                }
            }

            return ChunkerHelper.IsHashTypeChunk(hashType) ? hashType : ChunkerHelper.DefaultChunkHashType;
        }
    }
}