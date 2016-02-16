﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Protocol.Core.Types;

namespace NuGet.PackageManagement.UI
{
    internal class PackageItemLoader : IItemLoader<PackageItemListViewModel>
    {
        private readonly PackageLoadContext _context;
        private readonly string _searchText;
        private readonly bool _includePrerelease;

        private readonly IPackageFeed _packageFeed;
        private PackageCollection _installedPackages;

        private SearchFilter SearchFilter => new SearchFilter
        {
            IncludePrerelease = _includePrerelease,
            SupportedFrameworks = _context.GetSupportedFrameworks()
        };

        // Never null
        private PackageFeedSearchState _state = new PackageFeedSearchState();

        public IItemLoaderState State => _state;

        private class PackageFeedSearchState : IItemLoaderState
        {
            private readonly SearchResult<IPackageSearchMetadata> _results;

            public PackageFeedSearchState(SearchResult<IPackageSearchMetadata> results = null)
            {
                _results = results;
            }

            public SearchResult<IPackageSearchMetadata> Results => _results;

            public LoadingStatus LoadingStatus => AggregateLoadingStatus(SourceLoadingStatus?.Values);

            public bool IsMultiSource => SourceLoadingStatus?.Count > 1;

            public int ItemsCount => _results?.Items?.Count() ?? 0;

            public IDictionary<string, LoadingStatus> SourceLoadingStatus => _results?.SourceSearchStatus;

            private static LoadingStatus AggregateLoadingStatus(IEnumerable<LoadingStatus> statuses)
            {
                var count = statuses?.Count() ?? 0;

                if (count == 0)
                {
                    return LoadingStatus.Ready;
                }

                var first = statuses.First();
                if (count == 1 || statuses.All(x => x == first))
                {
                    return first;
                }

                if (statuses.Contains(LoadingStatus.Loading))
                {
                    return LoadingStatus.Loading;
                }

                if (statuses.Contains(LoadingStatus.ErrorOccured))
                {
                    return LoadingStatus.ErrorOccured;
                }

                if (statuses.Contains(LoadingStatus.Cancelled))
                {
                    return LoadingStatus.Cancelled;
                }

                return LoadingStatus.Unknown;
            }
        }

        public PackageItemLoader(
            PackageLoadContext context,
            IPackageFeed packageFeed,
            string searchText = null,
            bool includePrerelease = true)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            _context = context;

            if (packageFeed == null)
            {
                throw new ArgumentNullException(nameof(packageFeed));
            }
            _packageFeed = packageFeed;

            _searchText = searchText ?? string.Empty;
            _includePrerelease = includePrerelease;
        }

        public async Task<int> GetTotalCountAsync(int maxCount, CancellationToken cancellationToken)
        {
            int totalCount = 0;
            ContinuationToken nextToken = null;
            do
            {
                var searchResult = await SearchAsync(nextToken, cancellationToken);
                while (searchResult.RefreshToken != null)
                {
                    searchResult = await _packageFeed.RefreshSearchAsync(searchResult.RefreshToken, cancellationToken);
                }
                totalCount += searchResult.Items?.Count() ?? 0;
                nextToken = searchResult.NextToken;
            } while (nextToken != null && totalCount <= maxCount);

            return totalCount;
        }

        public async Task LoadNextAsync(IProgress<IItemLoaderState> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            NuGetEventTrigger.Instance.TriggerEvent(NuGetEvent.PackageLoadBegin);

            var searchResult = await SearchAsync(_state.Results?.NextToken, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            await UpdateStateAndReportAsync(searchResult, progress);

            NuGetEventTrigger.Instance.TriggerEvent(NuGetEvent.PackageLoadEnd);
        }

        public async Task UpdateStateAsync(IProgress<IItemLoaderState> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            NuGetEventTrigger.Instance.TriggerEvent(NuGetEvent.PackageLoadBegin);

            var refreshToken = _state.Results?.RefreshToken;
            if (refreshToken != null)
            {
                var searchResult = await _packageFeed.RefreshSearchAsync(refreshToken, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                await UpdateStateAndReportAsync(searchResult, progress);
            }

            NuGetEventTrigger.Instance.TriggerEvent(NuGetEvent.PackageLoadEnd);
        }

        private async Task<SearchResult<IPackageSearchMetadata>> SearchAsync(ContinuationToken continuationToken, CancellationToken cancellationToken)
        {
            if (continuationToken != null)
            {
                return await _packageFeed.ContinueSearchAsync(continuationToken, cancellationToken);
            }

            return await _packageFeed.SearchAsync(_searchText, SearchFilter, cancellationToken);
        }

        private async Task UpdateStateAndReportAsync(SearchResult<IPackageSearchMetadata> searchResult, IProgress<IItemLoaderState> progress)
        {
            // cache installed packages here for future use
            _installedPackages = await _context.GetInstalledPackagesAsync();

            var state = new PackageFeedSearchState(searchResult);
            _state = state;
            progress?.Report(state);
        }

        public void Reset()
        {
            _state = new PackageFeedSearchState();
        }

        public IEnumerable<PackageItemListViewModel> GetCurrent()
        {
            if (_state.ItemsCount == 0)
            {
                return Enumerable.Empty<PackageItemListViewModel>();
            }

            var listItems = _state.Results
                .Select(metadata =>
                {
                    var listItem = new PackageItemListViewModel
                    {
                        Id = metadata.Identity.Id,
                        Version = metadata.Identity.Version,
                        IconUrl = metadata.IconUrl,
                        Author = metadata.Authors,
                        DownloadCount = metadata.DownloadCount,
                        Summary = metadata.Summary,
                        Versions = AsyncLazy.New(() => metadata.GetVersionsAsync())
                    };
                    listItem.UpdatePackageStatus(_installedPackages);

                    if (!_context.IsSolution && _context.PackageManagerProviders.Any())
                    {
                        listItem.ProvidersLoader = AsyncLazy.New(
                            () => AlternativePackageManagerProviders.CalculateAlternativePackageManagersAsync(
                                _context.PackageManagerProviders,
                                listItem.Id,
                                _context.Projects[0]));
                    }

                    return listItem;
                });

            return listItems.ToArray();
        }
    }
}