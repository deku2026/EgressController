namespace EgressController.Guardian;

public interface IGuardTrayUpdatePlatform
{
    bool NeedsUpdate();
    void Stage();
    void StopInstalledTray();
    void Publish();
    void Restore();
    void Cleanup();
}

/// <summary>Update only the companion UI. The protection service and its saved intent stay running.</summary>
public static class GuardTrayUpdater
{
    public static bool EnsureCurrent(IGuardTrayUpdatePlatform platform)
    {
        if (!platform.NeedsUpdate()) return false;
        try
        {
            platform.Stage(); // Validate the complete replacement before touching the installed UI.
            platform.StopInstalledTray();
            platform.Publish();
            return true;
        }
        catch (Exception updateError)
        {
            try { platform.Restore(); }
            catch (Exception restoreError) { throw new AggregateException("托盘更新与回滚均失败，守护服务状态未更改；请保留备份并查看详细原因。", updateError, restoreError); }
            throw;
        }
        finally { platform.Cleanup(); }
    }
}
