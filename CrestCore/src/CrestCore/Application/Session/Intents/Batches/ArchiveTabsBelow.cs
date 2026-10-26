using CrestCore.Application;
using CrestCore.Domain;

namespace CrestCore.Contracts;

/// Archives the open tabs the sidebar lists below the open tab `TabId`: the
/// rest of its folder, then every tab and folder's tabs below, collapsed or
/// not. Its split partners, the tabs above it and saved and pinned tabs stay.
/// A window that showed an archived tab then shows `TabId`. Refused with
/// `CurrentTabsOnly` for a saved or pinned tab, and with `NoTabsBelow` when
/// no open tab is below it.
public sealed record ArchiveTabsBelow(Guid WorkspaceId, Guid WindowId, Guid SpaceId, Guid TabId) : SessionIntent(WorkspaceId) {
    #region Actions - Session

    /// Archives the open tabs the Space's sidebar lists below the tab. A
    /// window that showed one of them shows the tab instead; one that showed
    /// none keeps what it showed.
    internal override SessionEdit? Edit(NativeSessionAuthority workspace, SessionTurn turn) {
        var space = workspace.Editable(turn.Basis, SpaceId);
        var edited = BrowserTabCollection.Restore(space);
        var tab = edited.Tab(TabId);
        if (tab.Placement.IsDurable) throw new Rejected(new CurrentTabsOnly(tab.Id));
        var below = space.Sidebar.Below(tab.Id);
        if (below.Count == 0) throw new Rejected(new NoTabsBelow(tab.Id));
        var followUp = new WindowFollowUp(workspace.IssuingWindow(WindowId));
        var selected = edited.DismissTabs(below, followUp.Window?.Tab(space.Id), tab.Id, turn.Now, deleting: false,
            ensureSelection: false, resetArchivePlacement: false);
        edited.PruneSplitMetadata();
        followUp.ShowTab(space.Id, selected);
        return new(turn.Basis.Replacing(edited.Capture(space)), SyncStaging.Batch, followUp);
    }

    #endregion
}
