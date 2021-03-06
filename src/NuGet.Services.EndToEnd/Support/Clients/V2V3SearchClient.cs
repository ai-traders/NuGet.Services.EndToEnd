﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using NuGet.Services.AzureManagement;
using NuGet.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace NuGet.Services.EndToEnd.Support
{
    public class V2V3SearchClient
    {
        private const int MinPort = 44301;

        private readonly V3IndexClient _v3IndexClient;
        private readonly SimpleHttpClient _httpClient;
        private readonly TestSettings _testSettings;
        private readonly IRetryingAzureManagementAPIWrapper _azureManagementAPIWrapper;

        public V2V3SearchClient(SimpleHttpClient httpClient, V3IndexClient v3IndexClient, TestSettings testSettings, IRetryingAzureManagementAPIWrapper azureManagementAPIWrapper)
        {
            _httpClient = httpClient;
            _v3IndexClient = v3IndexClient;
            _testSettings = testSettings;
            _azureManagementAPIWrapper = azureManagementAPIWrapper;
        }

        public async Task<V3SearchResponse> QueryAsync(SearchServiceProperties searchService, string queryString, ITestOutputHelper logger)
        {
            var queryUrl = new Uri(searchService.Uri, $"query?{queryString}");
            return await _httpClient.GetJsonAsync<V3SearchResponse>(queryUrl.AbsoluteUri, logger);
        }

        public async Task<AutocompleteResponse> AutocompletePackageIdsAsync(
            SearchServiceProperties searchService,
            string packageId,
            bool includePrerelease,
            string semVerLevel,
            ITestOutputHelper logger)
        {
            var queryString = BuildAutocompleteQueryString($"take=30&q={packageId}", includePrerelease, semVerLevel);
            var queryUrl = new Uri(searchService.Uri, queryString);
            return await _httpClient.GetJsonAsync<AutocompleteResponse>(queryUrl.AbsoluteUri, logger);
        }

        public async Task<AutocompleteResponse> AutocompletePackageVersionsAsync(
            SearchServiceProperties searchService,
            string packageId,
            bool includePrerelease,
            string semVerLevel,
            ITestOutputHelper logger)
        {
            var queryString = BuildAutocompleteQueryString($"id={packageId}", includePrerelease, semVerLevel);
            var queryUrl = new Uri(searchService.Uri, queryString);
            return await _httpClient.GetJsonAsync<AutocompleteResponse>(queryUrl.AbsoluteUri, logger);
        }

        /// <summary>
        /// Polls all V2 search URLs until the provided ID and version are available. If <see cref="TestSettings.SearchBaseUrl"/>,
        /// is configured, then only that search service is polled. We poll the V2 search URL because it allows us to
        /// easily ignore all filtering rules and query for a single version.
        /// </summary>
        /// <param name="id">The package ID.</param>
        /// <param name="version">The package version.</param>
        /// <param name="logger">The logger.</param>
        /// <returns>Returns a task that completes when the package is available or the timeout has occurred.</returns>
        public async Task WaitForPackageAsync(string id, string version, ITestOutputHelper logger)
        {
            await PollAsync(
                id,
                version,
                response => response
                    .Data
                    .Where(d => d.PackageRegistration?.Id == id && d.Version == version)
                    .Any(),
                $"Waiting for package {id} {version} to be available on search endpoints:",
                $"Package {id} {version} was found on {{0}} after waiting {{1}}.",
                $"Package {id} {version} was not found on {{0}} after waiting {{1}}.",
                logger);
        }

        /// <summary>
        /// Polls all V2 search URLs until the package has the specified listed state.
        /// </summary>
        /// <param name="id">The package ID.</param>
        /// <param name="version">The package version.</param>
        /// <param name="listed">The listed state to wait for.</param>
        /// <param name="logger">The logger.</param>
        /// <returns>Returns a task that completes when the package is available or the timeout has occurred.</returns>
        public async Task WaitForListedStateAsync(string id, string version, bool listed, ITestOutputHelper logger)
        {
            var successState = listed ? "listed" : "unlisted";
            var failureState = listed ? "unlisted" : "listed";

            await PollAsync(
                id,
                version,
                response => response
                    .Data
                    .Where(d => d.PackageRegistration?.Id == id && d.Version == version && d.Listed == listed)
                    .Any(),
                $"Waiting for package {id} {version} to be {successState} on search endpoints:",
                $"Package {id} {version} became {successState} on {{0}} after waiting {{1}}.",
                $"Package {id} {version} was still {failureState} on {{0}} after waiting {{1}}.",
                logger);
        }

        private IEnumerable<string> GetSearchUrlsForPolling(SearchServiceProperties searchServices)
        {
            for (var instanceIndex = 0; instanceIndex < searchServices.InstanceCount; instanceIndex++)
            {
                var port = MinPort + instanceIndex;
                var uriBuilder = new UriBuilder(searchServices.Uri)
                {
                    Scheme = "https",
                    Port = port,
                    Path = "/search/query"
                };

                yield return uriBuilder.Uri.ToString();
            }
        }

        public async Task<IReadOnlyList<SearchServiceProperties>> GetSearchServicesAsync(ITestOutputHelper logger)
        {
            var searchServices = new List<SearchServiceProperties>();

            if (_azureManagementAPIWrapper == null)
            {
                var searchBaseUrls = await _v3IndexClient.GetSearchBaseUrlsAsync(logger);

                logger.WriteLine($"Configured search service mode: use index.json search services and use hardcoded" +
                                 $" instance count({_testSettings.SearchServiceConfiguration.OverrideInstanceCount}).Services: { string.Join(", ", searchBaseUrls)}");

                if (_testSettings.SearchServiceConfiguration.OverrideInstanceCount == 0)
                {
                    throw new ArgumentException(nameof(_testSettings.SearchServiceConfiguration.OverrideInstanceCount));
                }

                searchServices.AddRange(searchBaseUrls.Select(url => new SearchServiceProperties(new Uri(url), _testSettings.SearchServiceConfiguration.OverrideInstanceCount))
                                               .ToList());
            }
            else
            {
                if (_testSettings.SearchServiceConfiguration.IndexJsonMappedSearchServices != null)
                {
                    var searchBaseUrls = await _v3IndexClient.GetSearchBaseUrlsAsync(logger);

                    logger.WriteLine($"Configured search service mode: use index.json search services and get service" +
                                     $" properties from Azure. Services: { string.Join(", ", searchBaseUrls)}");

                    foreach (var url in searchBaseUrls)
                    {
                        // Clean the URL
                        var host = new Uri(url).Host;

                        if (!_testSettings.SearchServiceConfiguration.IndexJsonMappedSearchServices.ContainsKey(host))
                        {
                            throw new ArgumentException($"IndexJsonMappedSearchServices doesn't contain map for service {host}");
                        }

                        var mappedService = _testSettings.SearchServiceConfiguration.IndexJsonMappedSearchServices[host];

                        searchServices.Add(await GetSearchServiceFromAzureAsync(mappedService, logger));
                    }
                }
                else
                {
                    logger.WriteLine($"Configured search service mode: use single search service.");
                    searchServices.Add(await GetSearchServiceFromAzureAsync(_testSettings.SearchServiceConfiguration.SingleSearchService, logger));
                }
            }

            return searchServices;
        }

        private async Task<SearchServiceProperties> GetSearchServiceFromAzureAsync(AzureCloudServiceDetails serviceDetails, ITestOutputHelper logger)
        {
            logger.WriteLine($"Extracting search service properties from Azure. " +
                   $"Subscription: {serviceDetails.Subscription}, " +
                   $"Resource group: {serviceDetails.ResourceGroup}, " +
                   $"Service name: {serviceDetails.Name}");

            string result = await _azureManagementAPIWrapper.GetCloudServicePropertiesAsync(
                                serviceDetails.Subscription,
                                serviceDetails.ResourceGroup,
                                serviceDetails.Name,
                                serviceDetails.Slot,
                                logger,
                                CancellationToken.None);

            var cloudService = AzureHelper.ParseCloudServiceProperties(result);

            return new SearchServiceProperties(ClientHelper.ConvertToHttpsAndClean(cloudService.Uri), cloudService.InstanceCount);
        }

        private static string BuildAutocompleteQueryString(
            string query,
            bool? includePrerelease,
            string semVerLevel = null)
        {
            query += $"&prerelease={includePrerelease ?? false}";

            NuGetVersion semVerLevelVersion;
            if (!string.IsNullOrEmpty(semVerLevel) && NuGetVersion.TryParse(semVerLevel, out semVerLevelVersion))
            {
                query += $"&semVerLevel={semVerLevel}";
            }

            if (string.IsNullOrEmpty(query))
            {
                return string.Empty;
            }

            return "autocomplete?" + query.TrimStart('&');
        }

        private Task PollAsync(
            string id,
            string version,
            Func<V2SearchResponse, bool> isComplete,
            string startingMessage,
            string successMessageFormat,
            string failureMessageFormat,
            ITestOutputHelper logger)
        {
            // We perform the retry at this level so that we can re-fetch the list of search service instances from
            // Azure Management API. This list can change during scale-up or scale-down events.
            return RetryUtility.ExecuteWithRetry(
                async () =>
                {
                    var searchServices = await GetSearchServicesAsync(logger);

                    var v2SearchUrls = searchServices
                        .SelectMany(GetSearchUrlsForPolling)
                        .ToList();

                    Assert.True(v2SearchUrls.Count > 0, "At least one search base URL must be configured.");

                    logger.WriteLine(
                        startingMessage +
                        Environment.NewLine +
                        string.Join(Environment.NewLine, v2SearchUrls.Select(u => $" - {u}")));

                    var tasks = v2SearchUrls
                        .Select(u => PollAsync(
                            u,
                            id,
                            version,
                            isComplete,
                            successMessageFormat,
                            failureMessageFormat,
                            logger))
                        .ToList();

                    await Task.WhenAll(tasks);
                },
                ex => ex.HasTypeOrInnerType<SocketException>()
                   || ex.HasTypeOrInnerType<TaskCanceledException>(),
                logger: logger);

        }

        private async Task PollAsync(
            string v2SearchUrl,
            string id,
            string version,
            Func<V2SearchResponse, bool> isComplete,
            string successMessageFormat,
            string failureMessageFormat,
            ITestOutputHelper logger)
        {
            await Task.Yield();

            var url = QueryHelpers.AddQueryString(v2SearchUrl, "q", $"packageid:{id} and version:{version}");
            url = QueryHelpers.AddQueryString(url, "ignoreFilter", "true");
            url = QueryHelpers.AddQueryString(url, "semVerLevel", "2.0.0");

            var duration = Stopwatch.StartNew();
            var complete = false;
            do
            {
                var response = await _httpClient.GetJsonAsync<V2SearchResponse>(
                    url,
                    allowNotFound: false,
                    logResponseBody: false,
                    logger: logger);

                complete = isComplete(response);

                if (!complete && duration.Elapsed + TestData.V3SleepDuration < TestData.SearchWaitDuration)
                {
                    await Task.Delay(TestData.V3SleepDuration);
                }
            }
            while (!complete && duration.Elapsed < TestData.SearchWaitDuration);

            Assert.True(complete, string.Format(failureMessageFormat, url, duration.Elapsed));
            logger.WriteLine(string.Format(successMessageFormat, url, duration.Elapsed));
        }

        public class V2SearchResponse
        {
            public List<V2SearchPackage> Data { get; set; }
            public string Index { get; set; }
            public long TotalHits { get; set; }
        }

        public class V2SearchPackage
        {
            public V2SearchPackageRegistration PackageRegistration { get; set; }
            public string Version { get; set; }
            public string NormalizedVersion { get; set; }
            public string Title { get; set; }
            public string Description { get; set; }
            public string Summary { get; set; }
            public string Authors { get; set; }
            public string Copyright { get; set; }
            public string Tags { get; set; }
            public string ReleaseNotes { get; set; }
            public bool IsLatestStable { get; set; }
            public bool IsLatest { get; set; }
            public bool Listed { get; set; }
            public DateTimeOffset Created { get; set; }
            public DateTimeOffset Published { get; set; }
            public DateTimeOffset LastUpdated { get; set; }
            public DateTimeOffset LastEdited { get; set; }
            public long DownloadCount { get; set; }
            public string FlattenedDependencies { get; set; }
            public List<V2Dependency> Dependencies { get; set; }
            public string[] SupportedFrameworks { get; set; }
            public string Hash { get; set; }
            public string HashAlgorithm { get; set; }
            public long PackageFileSize { get; set; }
            public bool RequiresLicenseAcceptance { get; set; }
        }

        public class V2Dependency
        {
            public string Id { get; set; }
            public string VersionSpec { get; set; }
            public string TargetFramework { get; set; }
        }

        public class V2SearchPackageRegistration
        {
            public string Id { get; set; }
            public long DownloadCount { get; set; }
            public string[] Owners { get; set; }
        }

        public class V3SearchResponse
        {
            public List<V3SearchPackage> Data { get; set; }
            public string Index { get; set; }
            public long TotalHits { get; set; }
            public DateTimeOffset LastReopen { get; set; }
        }

        public class V3SearchPackage
        {
            public string Registration { get; set; }
            public string Id { get; set; }
            public string Version { get; set; }
            public string Description { get; set; }
            public string Summary { get; set; }
            public string Title { get; set; }
            public string[] Tags { get; set; }
            public string[] Authors { get; set; }
            public long TotalDownloads { get; set; }
            public List<V3VersionEntry> Versions { get; set; }
        }

        public class V3VersionEntry
        {
            public string Version { get; set; }
            public long Downloads { get; set; }
        }

        public class AutocompleteResponse
        {
            public List<string> Data { get; set; }
            public string Index { get; set; }
            public long TotalHits { get; set; }
            public DateTimeOffset LastReopen { get; set; }
        }
    }
}
