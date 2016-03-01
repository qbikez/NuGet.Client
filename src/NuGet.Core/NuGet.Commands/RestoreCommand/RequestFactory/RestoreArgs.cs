// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Logging;
using NuGet.Packaging;
using NuGet.ProjectModel;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol.Core.v3;

namespace NuGet.Commands
{
    public class RestoreArgs
    {
        public string ConfigFileName { get; set; }

        public IMachineWideSettings MachineWideSettings { get; set; }

        public string GlobalPackagesFolder { get; set; }

        public bool DisableParallel { get; set; }

        public HashSet<string> Runtimes { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        public HashSet<string> FallbackRuntimes { get; set; }

        public List<string> Inputs { get; set; } = new List<string>();

        public SourceCacheContext CacheContext { get; set; }

        public ILogger Log { get; set; }

        public List<string> Sources { get; set; } = new List<string>();

        public List<string> FallbackSources { get; set; } = new List<string>();

        public CachingSourceProvider CachingSourceProvider { get; set; }

        public List<IRestoreRequestProvider> RequestProviders { get; set; } = new List<IRestoreRequestProvider>();

        public PackageSaveMode PackageSaveMode { get; set; } = PackageSaveMode.Defaultv3;

        // Cache directory -> ISettings
        private ConcurrentDictionary<string, ISettings> _settingsCache
            = new ConcurrentDictionary<string, ISettings>(StringComparer.Ordinal);

        // ISettings -> PackageSourceProvider
        private ConcurrentDictionary<ISettings, PackageSourceProvider> _sourceProviderCache
            = new ConcurrentDictionary<ISettings, PackageSourceProvider>();

        public ISettings GetSettings(string projectDirectory)
        {
            // Ignore settings files inside the project directory itself, instead use the parent folder which 
            // can be shared and cached between projects.
            var parent = Directory.GetParent(projectDirectory);

            if (parent == null)
            {
                // If the projet was somehow at the root of the drive, just use the project dir
                parent = new DirectoryInfo(projectDirectory);
            }

            var parentDirectory = parent.FullName;

            return _settingsCache.GetOrAdd(parentDirectory, (dir) =>
            {
                return Settings.LoadDefaultSettings(dir,
                    ConfigFileName,
                    MachineWideSettings);
            });
        }

        public string GetEffectiveGlobalPackagesFolder(string rootDirectory, Lazy<ISettings> settings)
        {
            string globalPath = null;

            if (!string.IsNullOrEmpty(GlobalPackagesFolder))
            {
                globalPath = GlobalPackagesFolder;
            }
            else
            {
                globalPath = SettingsUtility.GetGlobalPackagesFolder(settings.Value);
            }

            // Resolve relative paths
            return Path.GetFullPath(Path.Combine(rootDirectory, globalPath));
        }

        /// <summary>
        /// Uses either Sources or Settings, and then adds Fallback sources.
        /// </summary>
        public List<SourceRepository> GetEffectiveSources(
            Lazy<ISettings> settings)
        {
            // Take the passed in sources
            var packageSources = Sources.Select(s => new PackageSource(s));

            var packageSourceProvider
                = new Lazy<PackageSourceProvider>(() =>
                _sourceProviderCache.GetOrAdd(settings.Value, (currentSettings) =>
                    new PackageSourceProvider(currentSettings)));

            // If no sources were passed in use the NuGet.Config sources
            if (!packageSources.Any())
            {
                // Add enabled sources
                packageSources = packageSourceProvider.Value.LoadPackageSources().Where(source => source.IsEnabled);
            }

            packageSources = packageSources.Concat(
                FallbackSources.Select(s => new PackageSource(s)));

            var cachingProvider = CachingSourceProvider ?? new CachingSourceProvider(packageSourceProvider.Value);

            return packageSources.Select(source => cachingProvider.CreateRepository(source))
                .Distinct()
                .ToList();
        }

        public void ApplyStandardProperties(RestoreRequest request)
        {
            request.PackageSaveMode = PackageSaveMode;

            var lockFilePath = ProjectJsonPathUtilities.GetLockFilePath(request.Project.FilePath);
            request.LockFilePath = lockFilePath;

            request.MaxDegreeOfConcurrency =
                DisableParallel ? 1 : RestoreRequest.DefaultDegreeOfConcurrency;
        }
    }
}
