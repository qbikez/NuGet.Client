﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NuGet.LibraryModel;
using NuGet.Logging;
using NuGet.ProjectModel;

namespace NuGet.Commands
{
    public class RestoreResult : IRestoreResult
    {
        public bool Success { get; }

        public MSBuildRestoreResult MSBuild { get; }

        /// <summary>
        /// Gets the path that the lock file will be written to.
        /// </summary>
        public string LockFilePath { get; set; }

        /// <summary>
        /// Gets the resolved dependency graphs produced by the restore operation
        /// </summary>
        public IEnumerable<RestoreTargetGraph> RestoreGraphs { get; }

        public IEnumerable<CompatibilityCheckResult> CompatibilityCheckResults { get; }

        public IEnumerable<ToolRestoreResult> ToolRestoreResults { get; }

        /// <summary>
        /// Gets a boolean indicating if the lock file will be re-written on <see cref="Commit"/>
        /// because the file needs to be re-locked.
        /// </summary>
        public bool RelockFile { get; }

        /// <summary>
        /// Gets the lock file that was generated during the restore or, in the case of a locked lock file,
        /// was used to determine the packages to install during the restore.
        /// </summary>
        public LockFile LockFile { get; }

        /// <summary>
        /// The existing lock file. This is null if no lock file was provided on the <see cref="RestoreRequest"/>.
        /// </summary>
        public LockFile PreviousLockFile { get; }

        public RestoreResult(
            bool success,
            IEnumerable<RestoreTargetGraph> restoreGraphs,
            IEnumerable<CompatibilityCheckResult> compatibilityCheckResults,
            LockFile lockFile,
            LockFile previousLockFile,
            string lockFilePath,
            MSBuildRestoreResult msbuild,
            IEnumerable<ToolRestoreResult> toolRestoreResults)
        {
            Success = success;
            RestoreGraphs = restoreGraphs;
            CompatibilityCheckResults = compatibilityCheckResults;
            LockFile = lockFile;
            LockFilePath = lockFilePath;
            MSBuild = msbuild;
            PreviousLockFile = previousLockFile;
            ToolRestoreResults = toolRestoreResults;
        }

        /// <summary>
        /// Calculates the complete set of all packages installed by this operation
        /// </summary>
        /// <remarks>
        /// This requires quite a bit of iterating over the graph so the result should be cached
        /// </remarks>
        /// <returns>A set of libraries that were installed by this operation</returns>
        public ISet<LibraryIdentity> GetAllInstalled()
        {
            return new HashSet<LibraryIdentity>(RestoreGraphs.Where(g => !g.InConflict).SelectMany(g => g.Install).Distinct().Select(m => m.Library));
        }

        /// <summary>
        /// Calculates the complete set of all unresolved dependencies for this operation
        /// </summary>
        /// <remarks>
        /// This requires quite a bit of iterating over the graph so the result should be cached
        /// </remarks>
        /// <returns>A set of dependencies that were unable to be resolved by this operation</returns>
        public ISet<LibraryRange> GetAllUnresolved()
        {
            return new HashSet<LibraryRange>(RestoreGraphs.SelectMany(g => g.Unresolved).Distinct());
        }

        /// <summary>
        /// Commits the lock file contained in <see cref="LockFile"/> and the MSBuild targets/props to
        /// the local file system.
        /// </summary>
        /// <remarks>If <see cref="PreviousLockFile"/> and <see cref="LockFile"/> are identical
        ///  the file will not be written to disk.</remarks>
        public void Commit(ILogger log)
        {
            Commit(log, forceWrite: false);
        }

        /// <summary>
        /// Commits the lock file contained in <see cref="LockFile"/> and the MSBuild targets/props to
        /// the local file system.
        /// </summary>
        /// <remarks>If <see cref="PreviousLockFile"/> and <see cref="LockFile"/> are identical
        ///  the file will not be written to disk.</remarks>
        /// <param name="forceWrite">Write out the lock file even if no changes exist.</param>
        public void Commit(ILogger log, bool forceWrite)
        {
            // Write the lock file
            var lockFileFormat = new LockFileFormat();

            Commit(lockFileFormat, this, log, forceWrite);

            foreach (var toolRestoreResult in ToolRestoreResults)
            {
                if (toolRestoreResult.LockFilePath != null && toolRestoreResult.LockFile != null)
                {
                    var lockFileDirectory = Path.GetDirectoryName(toolRestoreResult.LockFilePath);
                    Directory.CreateDirectory(lockFileDirectory);

                    Commit(lockFileFormat, toolRestoreResult, log, forceWrite);
                }
            }

            MSBuild.Commit(log);
        }

        private static void Commit(LockFileFormat lockFileFormat, IRestoreResult result, ILogger log, bool forceWrite)
        {
            // Don't write the lock file if it is Locked AND we're not re-locking the file
            if (!result.LockFile.IsLocked || result.RelockFile)
            {
                // Avoid writing out the lock file if it is the same to avoid triggering an intellisense
                // update on a restore with no actual changes.
                if (forceWrite
                    || result.PreviousLockFile == null
                    || !result.PreviousLockFile.Equals(result.LockFile))
                {
                    log.LogDebug($"Writing lock file to disk. Path: {result.LockFilePath}");

                    lockFileFormat.Write(result.LockFilePath, result.LockFile);
                }
                else
                {
                    log.LogDebug($"Lock file has not changed. Skipping lock file write. Path: {result.LockFilePath}");
                }
            }
        }
    }
}
