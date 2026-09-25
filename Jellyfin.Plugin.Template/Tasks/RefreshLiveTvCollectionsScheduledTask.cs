using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Template.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Template.Tasks;

/// <summary>
/// Manual task for refreshing Live TV collections.
/// </summary>
public sealed class RefreshLiveTvCollectionsScheduledTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly LiveTvCollectionSyncService _syncService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshLiveTvCollectionsScheduledTask"/> class.
    /// </summary>
    /// <param name="syncService">The collection sync service.</param>
    public RefreshLiveTvCollectionsScheduledTask(LiveTvCollectionSyncService syncService)
    {
        _syncService = syncService;
    }

    /// <inheritdoc />
    public string Name => "Refresh Live TV Collections";

    /// <inheritdoc />
    public string Description => "Synchronizes TV collections from M3U group titles.";

    /// <inheritdoc />
    public string Category => "Live TV";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public string Key => "RefreshLiveTvCollections";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _syncService.RefreshAsync(cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
