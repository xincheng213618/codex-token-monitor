namespace CodexTokenMonitor;

internal sealed class ResetOpportunitySynchronizer
{
    private bool isSyncing;

    public async Task<ResetOpportunitySyncResult?> SyncAsync(CancellationToken cancellationToken = default)
    {
        if (isSyncing)
        {
            return null;
        }

        isSyncing = true;
        try
        {
            return await ResetOpportunityStore.SyncFromCodexAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            isSyncing = false;
        }
    }
}
