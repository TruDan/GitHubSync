﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Octokit;

namespace GitHubSync
{
    public class RepoSync
    {
        Action<string> log;
        List<string> labelsToApplyOnPullRequests;
        SyncMode syncMode;
        Credentials defaultCredentials;
        List<ManualSyncItem> manualSyncItems = new List<ManualSyncItem>();
        List<RepositoryInfo> sources = new List<RepositoryInfo>();
        List<RepositoryInfo> targets = new List<RepositoryInfo>();

        public RepoSync(
            Action<string> log = null,
            List<string> labelsToApplyOnPullRequests = null,
            SyncMode syncMode = SyncMode.IncludeAllByDefault,
            Credentials defaultCredentials = null)
        {
            this.log = log ?? Console.WriteLine;
            this.labelsToApplyOnPullRequests = labelsToApplyOnPullRequests;
            this.syncMode = syncMode;
            this.defaultCredentials = defaultCredentials;
        }

        public void AddBlob(string path, string target = null)
        {
            AddSourceItem(TreeEntryTargetType.Blob, path, target);
        }

        public void RemoveBlob(string path, string target = null)
        {
            RemoveSourceItem(TreeEntryTargetType.Blob, path, target);
        }

        public void AddSourceItem(TreeEntryTargetType type, string path, string target = null)
        {
            AddOrRemoveSourceItem(true, type, path, target);
        }

        public void RemoveSourceItem(TreeEntryTargetType type, string path, string target = null)
        {
            if (type == TreeEntryTargetType.Tree)
            {
                throw new NotSupportedException($"Removing a '{nameof(TreeEntryTargetType.Tree)}' isn't supported.");
            }

            AddOrRemoveSourceItem(false, type, path, target);
        }

        public void AddOrRemoveSourceItem(bool toBeAdded, TreeEntryTargetType type, string path, string target)
        {
            Guard.AgainstNullAndEmpty(path, nameof(path));
            Guard.AgainstEmpty(target, nameof(target));

            if (toBeAdded && syncMode == SyncMode.IncludeAllByDefault)
            {
                throw new NotSupportedException($"Adding items is not supported when mode is '{syncMode}'");
            }

            if (!toBeAdded && syncMode == SyncMode.ExcludeAllByDefault)
            {
                throw new NotSupportedException($"Adding items is not supported when mode is '{syncMode}'");
            }

            manualSyncItems.Add(new ManualSyncItem
            {
                Path = path,
                Target = target
            });
        }

        public void AddSourceRepository(RepositoryInfo sourceRepository)
        {
            sources.Add(sourceRepository);
        }

        public void AddSourceRepository(string owner, string repository, string branch, Credentials credentials = null)
        {
            PerhapsDefault(ref credentials);
            sources.Add(new RepositoryInfo(credentials, owner, repository, branch));
        }

        public void AddTargetRepository(RepositoryInfo targetRepository)
        {
            targets.Add(targetRepository);
        }

        public void AddTargetRepository(string owner, string repository, string branch, Credentials credentials = null)
        {
            PerhapsDefault(ref credentials);
            targets.Add(new RepositoryInfo(credentials, owner, repository, branch));
        }

        void PerhapsDefault(ref Credentials credentials)
        {
            if (credentials != null)
            {
                return;
            }

            if (defaultCredentials == null)
            {
                throw new Exception("defaultCredentials required");
            }

            credentials = defaultCredentials;
        }

        public async Task<SyncContext> CalculateSyncContext(RepositoryInfo targetRepository)
        {
            var syncContext = new SyncContext(targetRepository);

            using (var syncer = new Syncer(targetRepository.Credentials, null, log))
            {
                var diffs = new List<Mapper>();
                var includedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var descriptionBuilder = new StringBuilder();
                descriptionBuilder.AppendLine("This is an automated synchronization PR.");
                descriptionBuilder.AppendLine();
                descriptionBuilder.AppendLine("The following source template repositories were used:");

                // Note: iterate backwards, later registered sources should override earlier registrations
                for (var i = sources.Count - 1; i >= 0; i--)
                {
                    var source = sources[i];
                    var displayName = $"{source.Owner}/{source.Repository}";
                    var itemsToSync = new List<SyncItem>();

                    foreach (var item in await OctokitEx.GetRecursive(source.Credentials, source.Owner, source.Repository, null, source.Branch))
                    {
                        if (includedPaths.Contains(item))
                        {
                            continue;
                        }

                        includedPaths.Add(item);

                        ProcessItem(item, itemsToSync, source);
                    }

                    var targetRepositoryToSync = new RepoToSync
                    {
                        Owner = targetRepository.Owner,
                        Repo = targetRepository.Repository,
                        TargetBranch = targetRepository.Branch
                    };

                    var sourceMapper = targetRepositoryToSync.GetMapper(itemsToSync);
                    var diff = await syncer.Diff(sourceMapper);
                    if (diff.ToBeAddedOrUpdatedEntries.Any() ||
                        diff.ToBeRemovedEntries.Any())
                    {
                        diffs.Add(diff);

                        descriptionBuilder.AppendLine($"* {displayName}");
                    }
                }

                var finalDiff = new Mapper();

                foreach (var diff in diffs)
                {
                    foreach (var item in diff.ToBeAddedOrUpdatedEntries)
                    {
                        foreach (var value in item.Value)
                        {
                            log($"Mapping '{item.Key.Url}' => '{value.Url}'");

                            finalDiff.Add(item.Key, value);
                        }
                    }

                    // Note: how to deal with items to be removed
                }

                syncContext.Diff = finalDiff;
                syncContext.Description = descriptionBuilder.ToString();
            }

            return syncContext;
        }

        void ProcessItem(string item, List<SyncItem> itemsToSync, RepositoryInfo source)
        {
            var parts = new Parts(
                $"{source.Owner}/{source.Repository}",
                TreeEntryTargetType.Blob, source.Branch, item);
            var localManualSyncItems = manualSyncItems.Where(x => item == x.Path).ToList();
            var isManualSyncItem = localManualSyncItems.Any();
            if (isManualSyncItem)
            {
                foreach (var manualSyncItem in localManualSyncItems)
                {
                    switch (syncMode)
                    {
                        case SyncMode.IncludeAllByDefault:
                            itemsToSync.Add(new SyncItem
                            {
                                Parts = parts,
                                ToBeAdded = false,
                                Target = manualSyncItem?.Target
                            });
                            continue;
                        case SyncMode.ExcludeAllByDefault:
                            itemsToSync.Add(new SyncItem
                            {
                                Parts = parts,
                                ToBeAdded = true,
                                Target = manualSyncItem?.Target
                            });
                            continue;
                    }
                }
                return;
            }
            switch (syncMode)
            {
                case SyncMode.IncludeAllByDefault:
                    itemsToSync.Add(new SyncItem
                    {
                        Parts = parts,
                        ToBeAdded = true,
                    });
                    return;
                case SyncMode.ExcludeAllByDefault:
                    itemsToSync.Add(new SyncItem
                    {
                        Parts = parts,
                        ToBeAdded = false,
                    });
                    return;
            }
        }

        public async Task<IReadOnlyList<UpdateResult>> Sync(SyncOutput syncOutput = SyncOutput.CreatePullRequest)
        {
            var list = new List<UpdateResult>();
            foreach (var targetRepository in targets)
            {
                var targetRepositoryDisplayName = $"{targetRepository.Owner}/{targetRepository.Repository}";

                using var syncer = new Syncer(targetRepository.Credentials, null, log);
                if (!await syncer.CanSynchronize(targetRepository, syncOutput))
                {
                    continue;
                }

                var syncContext = await CalculateSyncContext(targetRepository);

                if (!syncContext.Diff.ToBeAddedOrUpdatedEntries.Any())
                {
                    log($"Repo {targetRepositoryDisplayName} is in sync");
                    continue;
                }

                var sync = await syncer.Sync(syncContext.Diff, syncOutput, labelsToApplyOnPullRequests, syncContext.Description);
                var createdSyncBranch = sync.FirstOrDefault();

                if (createdSyncBranch == null)
                {
                    log($"Repo {targetRepositoryDisplayName} is in sync");
                }
                else
                {
                    log($"Pull created for {targetRepositoryDisplayName}, click here to review and pull: {createdSyncBranch}");
                }
                list.Add(createdSyncBranch);
            }

            return list;
        }
    }
}