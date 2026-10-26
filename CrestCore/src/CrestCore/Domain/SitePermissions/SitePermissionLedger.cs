using CrestCore.Contracts;

namespace CrestCore.Domain;

/// Per-Space site permission choices.
///
/// Persistent choices are the ledger's records; session choices override them
/// until the process ends and are never kept. The Spaces of a group share one
/// session, so the group's choices answer for each of them: commands and
/// lookups name a Space's group, the Space that keeps the group's new choices
/// first, and a lookup takes the latest choice any of them kept. A request with a detail (such as
/// one URL scheme) is answered by the narrowest choice first, then by the
/// site-wide rule for the same capability. A locked Space never answers: its
/// questions are Ask. Removals always apply, so deleting or resetting a Space
/// cannot be blocked by its lock. Callers serialize access.
public sealed class SitePermissionLedger {
    #region Static Variables

    public const int MaximumRecords = 4_096;
    public const int MaximumDetailLength = 256;

    #endregion

    #region Variables

    private readonly List<SitePermissionRecord> persistent = [];
    private readonly Dictionary<Guid, Dictionary<SitePermissionKey, SitePermissionDecision>> session = [];

    /// The persistent records in storage order.
    public IReadOnlyList<SitePermissionRecord> PersistentRecords => persistent;

    /// The Spaces that hold a persistent record.
    public IEnumerable<Guid> Spaces => persistent.Select(record => record.Space).Distinct();

    #endregion

    #region Actions - Persistence

    /// Replaces the persistent records with `records`, in order. Records that
    /// are not persistent choices, grant a capability the system asks about
    /// each time, or that repeat an earlier record's identity
    /// or its Space, origin, capability and detail, are left out, as lookups
    /// always answered from the first; so are records beyond the limit. Session
    /// choices stay. Returns how many records were kept.
    public int Restore(IEnumerable<SitePermissionRecord> records) {
        ArgumentNullException.ThrowIfNull(records);
        persistent.Clear();
        var seen = new HashSet<(Guid, SitePermissionKey)>();
        var identities = new HashSet<Guid>();
        foreach (var record in records) {
            if (persistent.Count >= MaximumRecords) break;
            if (record.Id == Guid.Empty || record.Space == Guid.Empty || !IsValidDetail(record.Detail) || !record.Origin.IsValid
                || !record.Decision.IsPersistent || !IsAvailable(record.Permission, record.Decision)
                || !double.IsFinite(record.ModifiedAt)) continue;
            if (!identities.Add(record.Id) || !seen.Add((record.Space, Key(record)))) continue;
            persistent.Add(record);
        }
        return persistent.Count;
    }

    #endregion

    #region Actions - Decisions

    /// The choice that answers one request in a Space of the group `spaces`
    /// names: a session answer, the first Space's first, else the latest
    /// choice any of them kept. A locked Space, an origin the rules cannot
    /// read and a detail that is not one answer Ask.
    public SitePermissionDecision Decision(IReadOnlyList<Guid> spaces, SiteOrigin origin, SitePermission permission, string? detail,
        bool isLocked) {
        ArgumentNullException.ThrowIfNull(spaces);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(permission);
        if (isLocked || !origin.IsValid || !IsValidDetail(detail)) return SitePermissionDecision.Ask;
        var key = new SitePermissionKey(origin, permission, detail);
        foreach (var candidate in detail is null ? [key] : new[] { key, key with { Detail = null } }) {
            foreach (var space in spaces)
                if (session.TryGetValue(space, out var decisions) && decisions.TryGetValue(candidate, out var decision)) return decision;
            if (Kept(spaces, candidate) is { } saved) return saved.Decision;
        }
        return SitePermissionDecision.Ask;
    }

    /// The latest record any of `spaces` keeps for `key`, or null.
    private SitePermissionRecord? Kept(IReadOnlyList<Guid> spaces, SitePermissionKey key) =>
        persistent.Where(record => spaces.Contains(record.Space) && Key(record) == key).MaxBy(record => record.ModifiedAt);

    /// A capture request's decision. Combined capture must respect a block on
    /// either device, and an existing combined grant still answers a request
    /// for just one of those devices. A capability that stands alone answers
    /// with its own decision.
    public SitePermissionDecision MediaDecision(IReadOnlyList<Guid> spaces, SiteOrigin origin, SitePermission media, bool isLocked) {
        ArgumentNullException.ThrowIfNull(media);
        if (isLocked) return SitePermissionDecision.Ask;
        var devices = media.Devices().Select(device => Decision(spaces, origin, device, null, false)).ToArray();
        var combinations = media.Combinations().Select(combination => Decision(spaces, origin, combination, null, false)).ToArray();
        var strongest = SitePermissionDecision.Strongest(devices.Concat(combinations));
        if (strongest.Denies) return strongest;
        return combinations.FirstOrDefault(decision => decision.Grants) ?? SitePermissionDecision.Strongest(devices);
    }

    /// The persistent records that answer for the group `spaces` names, in
    /// display order: the latest any of them keeps for each choice.
    public IReadOnlyList<SitePermissionRecord> Records(IReadOnlyList<Guid> spaces) =>
        [.. persistent.Where(record => spaces.Contains(record.Space)).GroupBy(Key).Select(kept => kept.MaxBy(record => record.ModifiedAt)!)
            .Order(SitePermissionRecordOrder.Instance)];

    #endregion

    #region Actions - Commands

    /// Records one answer for the group `spaces` names, in its first Space,
    /// clearing the same choice from the others, so the answer is the
    /// group's. Ask clears both the session and the saved choice; a session
    /// answer overrides the saved choice without replacing it; a persistent
    /// answer replaces both and keeps an existing record's identity.
    /// `recordId` names the record a new persistent choice creates, and `now`
    /// is when it was made, in seconds of the stored epoch. Throws `Rejected`
    /// for an origin or a detail the rules cannot read, for a grant of a
    /// capability the system asks about each time, and for a new record beyond
    /// the limit.
    public SitePermissionOutcome Set(IReadOnlyList<Guid> spaces, SiteOrigin origin, SitePermission permission, string? detail,
        SitePermissionDecision decision, Guid recordId, double now) {
        ArgumentNullException.ThrowIfNull(spaces);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(permission);
        ArgumentNullException.ThrowIfNull(decision);
        if (!origin.IsValid) throw new Rejected(new InvalidSiteOrigin(origin));
        if (!IsValidDetail(detail)) throw new Rejected(new InvalidSitePermissionDetail(MaximumDetailLength));
        if (!IsAvailable(permission, decision)) throw new Rejected(new InvalidSitePermissionGrant(permission));
        var key = new SitePermissionKey(origin, permission, detail);
        var space = spaces[0];
        bool persistenceChanged = decision.Record(this, space, key, recordId, now);
        foreach (var other in spaces.Skip(1)) persistenceChanged |= Forget(other, key);
        return new(persistenceChanged, [.. spaces.Select(member => new SitePermissionChange(member, new(origin, permission, detail,
            !decision.Grants)))]);
    }

    /// Saves `decision` for `key` in `space`, replacing the session answer and
    /// keeping an existing record's identity; a new record is `recordId`.
    internal bool Persist(Guid space, SitePermissionKey key, SitePermissionDecision decision, Guid recordId, double now) {
        int index = persistent.FindIndex(record => record.Space == space && Key(record) == key);
        if (index < 0 && persistent.Count >= MaximumRecords) throw new Rejected(new SitePermissionLimitReached(MaximumRecords));
        if (session.TryGetValue(space, out var overridden)) overridden.Remove(key);
        if (index >= 0) persistent[index] = persistent[index] with { Decision = decision, ModifiedAt = now };
        else persistent.Add(new(recordId, space, key.Origin, key.Permission, key.Detail, decision, now));
        return true;
    }

    /// Clears both the session answer and the saved choice for `key` in
    /// `space`, and says whether a saved one went.
    internal bool Forget(Guid space, SitePermissionKey key) {
        if (session.TryGetValue(space, out var cleared)) cleared.Remove(key);
        return persistent.RemoveAll(record => record.Space == space && Key(record) == key) > 0;
    }

    /// Answers `key` in `space` with `decision` until the process ends,
    /// over the saved choice, which stays.
    internal bool KeepForSession(Guid space, SitePermissionKey key, SitePermissionDecision decision) {
        if (!session.TryGetValue(space, out var decisions)) session[space] = decisions = [];
        decisions[key] = decision;
        return false;
    }

    /// Forgets the persistent record `id` and the same choice in every other
    /// Space of the group `group` gives for its Space; one that is gone
    /// changes nothing.
    public SitePermissionOutcome ResetRecord(Guid id, Func<Guid, IReadOnlyList<Guid>> group) {
        ArgumentNullException.ThrowIfNull(group);
        if (persistent.FirstOrDefault(record => record.Id == id) is not { } record) return SitePermissionOutcome.Unchanged;
        var spaces = group(record.Space);
        var key = Key(record);
        persistent.Remove(record);
        foreach (var space in spaces) Forget(space, key);
        return new(true, [.. spaces.Append(record.Space).Distinct()
            .Select(space => new SitePermissionChange(space, new(record.Origin, record.Permission, record.Detail, true)))]);
    }

    /// Clears every saved and session choice in the group `spaces` names. It
    /// always touches every Space of it, so a page withdraws what it was given
    /// and cancels a request still waiting for an answer, even when no choice
    /// was kept.
    public SitePermissionOutcome ResetSpace(IReadOnlyList<Guid> spaces) {
        ArgumentNullException.ThrowIfNull(spaces);
        foreach (var space in spaces) session.Remove(space);
        bool removed = persistent.RemoveAll(record => spaces.Contains(record.Space)) > 0;
        return new(removed, [.. spaces.Select(space => new SitePermissionChange(space, new(null, null, null, true)))]);
    }

    /// `space` joins the group whose other Spaces `group` names: where the
    /// group already chose for a site, its choice stands and the joining
    /// Space's own goes.
    public SitePermissionOutcome Joining(Guid space, IReadOnlyList<Guid> group) {
        ArgumentNullException.ThrowIfNull(group);
        var decided = persistent.Where(record => record.Space != space && group.Contains(record.Space)).Select(Key).ToHashSet();
        var overruled = persistent.Where(record => record.Space == space && decided.Contains(Key(record))).ToList();
        foreach (var record in overruled) persistent.Remove(record);
        return overruled.Count == 0 ? SitePermissionOutcome.Unchanged
            : new(true, [.. group.Append(space).Distinct().Select(member => new SitePermissionChange(member, new(null, null, null, true)))]);
    }

    /// `successor` keeps the choices `from` kept for the group it took over,
    /// in place of its own for the same sites.
    public SitePermissionOutcome HandingOver(Guid from, Guid successor) {
        var moved = persistent.Where(record => record.Space == from).ToList();
        if (moved.Count == 0) return SitePermissionOutcome.Unchanged;
        var keys = moved.Select(Key).ToHashSet();
        persistent.RemoveAll(record => record.Space == successor && keys.Contains(Key(record)));
        for (int index = 0; index < persistent.Count; index++)
            if (persistent[index].Space == from) persistent[index] = persistent[index] with { Space = successor };
        if (session.Remove(from, out var answers)) session[successor] = answers;
        return new(true, [new(from, new(null, null, null, true)), new(successor, new(null, null, null, true))]);
    }

    #endregion

    #region Actions - Keys

    private static SitePermissionKey Key(SitePermissionRecord record) => new(record.Origin, record.Permission, record.Detail);

    private static bool IsValidDetail(string? detail) => detail is null || (detail.Length is > 0 and <= MaximumDetailLength);

    /// Whether a site may be given `decision` for `permission`: the system
    /// asks about some capabilities each time, so nothing grants them ahead.
    private static bool IsAvailable(SitePermission permission, SitePermissionDecision decision) =>
        !(decision.Grants && permission.IsAskedBySystem);

    /// One choice's identity within a Space.
    internal readonly record struct SitePermissionKey(SiteOrigin Origin, SitePermission Permission, string? Detail);

    #endregion
}
