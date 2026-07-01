namespace CodexTokenMonitor;

internal sealed class ResetOpportunitySynchronizer
{
    private bool isSyncing;

    public async Task<ResetOpportunitySyncResult?> SyncAsync()
    {
        if (isSyncing)
        {
            return null;
        }

        isSyncing = true;
        try
        {
            return await ResetOpportunityStore.SyncFromCodexAsync();
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
