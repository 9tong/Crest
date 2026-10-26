using CrestCore.Application;

namespace CrestCore.Contracts;

/// The open tabs `ArchiveTabsBelow` would archive below the tab `TabId`, as a
/// window shows them before it acts: none for a saved or pinned tab, or a tab
/// the Space does not hold. A preview reads a locked Space as a person sees
/// it; only the action refuses it.
public sealed record TabsBelowPreview(Guid WorkspaceId, Guid SpaceId, Guid TabId) : Query<TabsBelow> {
    #region Actions - Answering

    internal override TabsBelow Answer(CrestApp app) => Answer(app.Device.Workspace(WorkspaceId));

    /// The open tabs the Space's sidebar lists below the tab, in sidebar
    /// order. A locked Space is read as a person sees it.
    private TabsBelow Answer(NativeSessionAuthority workspace) {
        SpaceState space;
        lock (NativeSessionAuthority.Gate) space = workspace.AcceptedSession.KnownSpace(SpaceId);
        if (space.Tabs.FirstOrDefault(tab => tab.Id == TabId) is not { } tab || tab.Placement.IsDurable) return new([]);
        return new(space.Sidebar.Below(tab.Id));
    }

    #endregion
}
