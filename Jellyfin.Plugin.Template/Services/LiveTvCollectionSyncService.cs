using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Template.Services;

/// <summary>
/// Synchronizes plugin-managed collections from M3U Live TV channel groups.
/// </summary>
public sealed class LiveTvCollectionSyncService : IHostedService, IDisposable
{
    private const string M3uServiceName = "M3U Tuner";
    private const string CollectionNamePrefix = "TV - ";
    private const string ProviderIdKey = "Jellyfin.Plugin.LiveTvCollections.GroupTitle";
    private const string RefreshChannelsTaskType = "Jellyfin.LiveTv.Channels.RefreshChannelsScheduledTask";

    private readonly ICollectionManager _collectionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LiveTvCollectionSyncService> _logger;
    private readonly ITaskManager _taskManager;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvCollectionSyncService"/> class.
    /// </summary>
    /// <param name="taskManager">The task manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="collectionManager">The collection manager.</param>
    /// <param name="logger">The logger.</param>
    public LiveTvCollectionSyncService(
        ITaskManager taskManager,
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        ILogger<LiveTvCollectionSyncService> logger)
    {
        _taskManager = taskManager;
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskCompleted += OnTaskCompleted;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskCompleted -= OnTaskCompleted;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Refreshes the collections from the current M3U channel groups.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the refresh is finished.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var channels = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Channel],
            Recursive = true,
            IsVirtualItem = false
        });

        var groups = channels
            .OfType<Channel>()
            .Where(IsManagedChannel)
            .Select(channel => new
            {
                Channel = channel,
                GroupTitle = GetManagedGroupTitle(channel)
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.GroupTitle))
            .GroupBy(entry => entry.GroupTitle!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Channel.Id).ToHashSet(),
                StringComparer.OrdinalIgnoreCase);

        var collections = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Recursive = true,
            IsVirtualItem = false
        })
            .OfType<BoxSet>()
            .Where(IsManagedCollection)
            .ToList();

        foreach (var collection in collections)
        {
            var groupTitle = GetManagedGroupTitle(collection);
            if (groupTitle is null)
            {
                continue;
            }

            if (!groups.ContainsKey(groupTitle))
            {
                await DeleteCollectionAsync(collection).ConfigureAwait(false);
            }
        }

        foreach (var group in groups)
        {
            var groupTitle = group.Key;
            var channelIds = group.Value;
            var collectionName = GetCollectionName(groupTitle);
            var collection = collections.FirstOrDefault(existing => string.Equals(GetManagedGroupTitle(existing), groupTitle, StringComparison.OrdinalIgnoreCase))
                ?? collections.FirstOrDefault(existing => string.Equals(existing.Name, collectionName, StringComparison.OrdinalIgnoreCase));

            if (collection is null)
            {
                collection = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                {
                    Name = collectionName,
                    ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [ProviderIdKey] = groupTitle
                    }
                }).ConfigureAwait(false);

                collections.Add(collection);
            }
            else if (!collection.ProviderIds.TryGetValue(ProviderIdKey, out var storedGroupTitle) ||
                     !string.Equals(storedGroupTitle, groupTitle, StringComparison.OrdinalIgnoreCase))
            {
                collection.ProviderIds[ProviderIdKey] = groupTitle;
                await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }

            await SyncCollectionItemsAsync(collection, channelIds).ConfigureAwait(false);
        }
    }

    private async Task SyncCollectionItemsAsync(BoxSet collection, HashSet<Guid> channelIds)
    {
        var currentIds = collection.LinkedChildren
            .Where(child => child.ItemId.HasValue)
            .Select(child => child.ItemId!.Value)
            .ToHashSet();

        var idsToAdd = channelIds.Except(currentIds).ToArray();
        if (idsToAdd.Length > 0)
        {
            await _collectionManager.AddToCollectionAsync(collection.Id, idsToAdd).ConfigureAwait(false);
        }

        var idsToRemove = currentIds.Except(channelIds).ToArray();
        if (idsToRemove.Length > 0)
        {
            await _collectionManager.RemoveFromCollectionAsync(collection.Id, idsToRemove).ConfigureAwait(false);
        }
    }

    private static bool IsManagedChannel(Channel channel)
        => string.Equals(channel.ServiceName, M3uServiceName, StringComparison.OrdinalIgnoreCase);

    private static bool IsManagedCollection(BoxSet collection)
        => collection.ProviderIds.ContainsKey(ProviderIdKey);

    private static string? GetManagedGroupTitle(BaseItem item)
    {
        if (item.ProviderIds.TryGetValue(ProviderIdKey, out var providerGroupTitle) && !string.IsNullOrWhiteSpace(providerGroupTitle))
        {
            return providerGroupTitle.Trim();
        }

        return null;
    }

    private static string GetCollectionName(string groupTitle)
        => CollectionNamePrefix + groupTitle;

    private Task DeleteCollectionAsync(BoxSet collection)
    {
        _logger.LogInformation("Removing stale Live TV collection {CollectionName}.", collection.Name);

        _libraryManager.DeleteItem(collection, new DeleteOptions { DeleteFileLocation = true });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _refreshLock.Dispose();
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (!string.Equals(e.Task.ScheduledTask.GetType().FullName, RefreshChannelsTaskType, StringComparison.Ordinal))
        {
            return;
        }

        if (e.Result.Status != TaskCompletionStatus.Completed)
        {
            return;
        }

        _ = RefreshAsync(CancellationToken.None);
    }
}
