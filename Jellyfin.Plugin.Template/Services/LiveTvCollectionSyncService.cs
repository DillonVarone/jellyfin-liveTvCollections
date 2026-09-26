using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Template.Services;

/// <summary>
/// Synchronizes plugin-managed collections from M3U Live TV channel groups.
/// </summary>
public sealed class LiveTvCollectionSyncService : IHostedService, IDisposable
{
    private const string CollectionNamePrefix = "TV - ";
    private const string ProviderIdKey = "ChannelGroup";
    private const string RefreshChannelsTaskType = "Jellyfin.LiveTv.Channels.RefreshChannelsScheduledTask";
    private const string RefreshGuideTaskType = "Jellyfin.LiveTv.Guide.RefreshGuideScheduledTask";
    private const string M3uParserTypeName = "Jellyfin.LiveTv.TunerHosts.M3uParser, Jellyfin.LiveTv";

    private readonly IConfigurationManager _configurationManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LiveTvCollectionSyncService> _logger;
    private readonly ITaskManager _taskManager;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvCollectionSyncService"/> class.
    /// </summary>
    /// <param name="taskManager">The task manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="collectionManager">The collection manager.</param>
    /// <param name="configurationManager">The configuration manager.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public LiveTvCollectionSyncService(
        ITaskManager taskManager,
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        IConfigurationManager configurationManager,
        IHttpClientFactory httpClientFactory,
        ILogger<LiveTvCollectionSyncService> logger)
    {
        _taskManager = taskManager;
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _configurationManager = configurationManager;
        _httpClientFactory = httpClientFactory;
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
        _logger.LogInformation("Starting Live TV collections refresh.");

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
        var liveTvOptions = _configurationManager.GetConfiguration<LiveTvOptions>("livetv");
        var m3uHosts = liveTvOptions.TunerHosts
            .Where(host => string.Equals(host.Type, "m3u", StringComparison.OrdinalIgnoreCase))
            .Where(host => !string.IsNullOrWhiteSpace(host.Url))
            .ToArray();

        _logger.LogInformation("Found {HostCount} configured M3U tuner host(s).", m3uHosts.Length);

        var parsedChannels = new List<ChannelInfo>();
        foreach (var host in m3uHosts)
        {
            var hostChannels = await ParseChannelsAsync(host, cancellationToken).ConfigureAwait(false);
            parsedChannels.AddRange(hostChannels);
            _logger.LogInformation("Parsed {ChannelCount} channel(s) from M3U tuner host {TunerHostId}.", hostChannels.Count, host.Id ?? host.Url);
        }

        _logger.LogInformation("Parsed {ChannelCount} total M3U channel(s) from all configured tuner hosts.", parsedChannels.Count);

        if (parsedChannels.Count == 0)
        {
            _logger.LogInformation("No channels were parsed from the configured M3U tuner hosts.");
        }

        var liveTvChannels = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvChannel],
            Recursive = true,
            IsVirtualItem = false
        })
            .OfType<LiveTvChannel>()
            .Where(channel => !string.IsNullOrWhiteSpace(channel.ExternalId))
            .ToDictionary(channel => channel.ExternalId, channel => channel.Id, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("Found {ChannelCount} Live TV channel item(s) with external IDs.", liveTvChannels.Count);

        var groups = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        var groupLogos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unmatchedChannelCount = 0;

        foreach (var channelInfo in parsedChannels)
        {
            if (string.IsNullOrWhiteSpace(channelInfo.ChannelGroup))
            {
                continue;
            }

            if (!liveTvChannels.TryGetValue(channelInfo.Id, out var existingChannelId))
            {
                unmatchedChannelCount++;
                continue;
            }

            if (!groups.TryGetValue(channelInfo.ChannelGroup, out var channelIds))
            {
                channelIds = new HashSet<Guid>();
                groups[channelInfo.ChannelGroup] = channelIds;
            }

            if (!groupLogos.ContainsKey(channelInfo.ChannelGroup) && !string.IsNullOrWhiteSpace(channelInfo.ImageUrl))
            {
                groupLogos[channelInfo.ChannelGroup] = channelInfo.ImageUrl!;
            }

            channelIds.Add(existingChannelId);
        }

        foreach (var group in groups)
        {
            _logger.LogInformation("Mapped M3U group {GroupTitle} to {ChannelCount} existing Live TV channel item(s).", group.Key, group.Value.Count);
        }

        _logger.LogInformation(
            "Matched {GroupCount} M3U group(s) to existing Live TV channels; {UnmatchedCount} parsed channel(s) did not resolve to a database entry.",
            groups.Count,
            unmatchedChannelCount);

        var collections = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Recursive = true,
            IsVirtualItem = false
        })
            .OfType<BoxSet>()
            .Where(IsManagedCollection)
            .ToList();

        _logger.LogInformation("Found {CollectionCount} existing managed Live TV collection(s) before reconciliation.", collections.Count);

        foreach (var collection in collections.ToArray())
        {
            var groupTitle = GetManagedGroupTitle(collection);
            if (groupTitle is null || groups.ContainsKey(groupTitle))
            {
                continue;
            }

            _logger.LogInformation("Deleting stale Live TV collection {CollectionName} for missing group {GroupTitle}.", collection.Name, groupTitle);
            await DeleteCollectionAsync(collection).ConfigureAwait(false);
            collections.Remove(collection);
        }

        foreach (var (groupTitle, channelIds) in groups)
        {
            var collectionName = GetCollectionName(groupTitle);
            var collection = collections.FirstOrDefault(existing => string.Equals(GetManagedGroupTitle(existing), groupTitle, StringComparison.OrdinalIgnoreCase))
                ?? collections.FirstOrDefault(existing => string.Equals(existing.Name, collectionName, StringComparison.OrdinalIgnoreCase));

            if (collection is null)
            {
                _logger.LogInformation("Creating Live TV collection {CollectionName} for group {GroupTitle} with {ChannelCount} channel(s).", collectionName, groupTitle, channelIds.Count);
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
                _logger.LogInformation("Updating Live TV collection {CollectionName} with group {GroupTitle}.", collection.Name, groupTitle);
                collection.ProviderIds[ProviderIdKey] = groupTitle;
                await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }

            await SyncCollectionItemsAsync(collection, channelIds).ConfigureAwait(false);

            if (groupLogos.TryGetValue(groupTitle, out var logoUrl) && !string.IsNullOrWhiteSpace(logoUrl))
            {
                _logger.LogInformation("Setting collection logo for {CollectionName} from {LogoUrl}.", collection.Name, logoUrl);
                await SetCollectionLogoAsync(collection, logoUrl, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Completed Live TV collections refresh. {CollectionCount} managed collection(s) processed.", groups.Count);
    }

    private async Task<List<ChannelInfo>> ParseChannelsAsync(TunerHostInfo host, CancellationToken cancellationToken)
    {
        var parserType = Type.GetType(M3uParserTypeName, throwOnError: false);
        if (parserType is null)
        {
            _logger.LogError("Unable to locate the Jellyfin Live TV M3U parser type {ParserType}.", M3uParserTypeName);
            return [];
        }

        var parserConstructor = parserType.GetConstructor(new[] { typeof(ILogger), typeof(IHttpClientFactory) });
        if (parserConstructor is null)
        {
            _logger.LogError("Unable to construct the Jellyfin Live TV M3U parser.");
            return [];
        }

        var parser = parserConstructor.Invoke(new object[] { _logger, _httpClientFactory });
        var parseMethod = parserType.GetMethod("Parse");
        if (parseMethod is null)
        {
            _logger.LogError("Unable to locate Parse on the Jellyfin Live TV M3U parser.");
            return [];
        }

        var channelIdPrefix = GetChannelIdPrefix(host);
        var result = parseMethod.Invoke(parser, new object[] { host, channelIdPrefix, cancellationToken });
        if (result is Task<List<ChannelInfo>> typedTask)
        {
            return await typedTask.ConfigureAwait(false);
        }

        _logger.LogError("Unexpected return type from Jellyfin Live TV M3U parser invocation.");
        return [];
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
            _logger.LogInformation("Adding {AddCount} channel item(s) to Live TV collection {CollectionName}.", idsToAdd.Length, collection.Name);
            await _collectionManager.AddToCollectionAsync(collection.Id, idsToAdd).ConfigureAwait(false);
        }

        var idsToRemove = currentIds.Except(channelIds).ToArray();
        if (idsToRemove.Length > 0)
        {
            _logger.LogInformation("Removing {RemoveCount} channel item(s) from Live TV collection {CollectionName}.", idsToRemove.Length, collection.Name);
            await _collectionManager.RemoveFromCollectionAsync(collection.Id, idsToRemove).ConfigureAwait(false);
        }
    }

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

    private static string GetChannelIdPrefix(TunerHostInfo host)
        => "m3u_" + host.Url.GetMD5().ToString("N", CultureInfo.InvariantCulture);

    private async Task SetCollectionLogoAsync(BoxSet collection, string logoUrl, CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            var imageBytes = await httpClient.GetByteArrayAsync(logoUrl, cancellationToken).ConfigureAwait(false);

            var imageInfo = new MediaBrowser.Controller.Entities.ItemImageInfo
            {
                Type = MediaBrowser.Model.Entities.ImageType.Primary,
                Path = logoUrl,
                DateModified = DateTime.UtcNow
            };

            collection.AddImage(imageInfo);
            await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Successfully set collection logo for {CollectionName}.", collection.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set collection logo for {CollectionName} from {LogoUrl}.", collection.Name, logoUrl);
        }
    }

    private Task DeleteCollectionAsync(BoxSet collection)
    {
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
        var taskType = e.Task.ScheduledTask.GetType().FullName;

        if (!string.Equals(taskType, RefreshChannelsTaskType, StringComparison.Ordinal)
            && !string.Equals(taskType, RefreshGuideTaskType, StringComparison.Ordinal))
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
