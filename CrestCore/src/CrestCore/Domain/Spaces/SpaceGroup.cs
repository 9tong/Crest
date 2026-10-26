using CrestCore.Contracts;

namespace CrestCore.Domain;

/// <summary>
/// A primary Space and the Sub-Spaces that joined its session, in order. Every member
/// browses with the primary's profile, and they sit together: the primary, then its
/// Sub-Spaces. A Space on its own is a group of one, and no two groups browse with one
/// profile.
/// </summary>
/// <remarks>
/// Only Spaces that stay count. A Sub-Space whose deletion is under way belongs to no
/// group and is left as it is; a primary whose deletion is under way still leads the
/// Sub-Spaces that stay until it is removed, and leads nothing once none stays.
/// </remarks>
internal sealed record SpaceGroup(SpaceState Primary, IReadOnlyList<SpaceState> SubSpaces) {
    #region Variables

    /// Collections are owned when constructed or replaced on a copy.
    public IReadOnlyList<SpaceState> SubSpaces {
        get;
        init => field = [.. value];
    } = [.. SubSpaces];

    /// The primary, then its Sub-Spaces.
    public IReadOnlyList<SpaceState> Members => [Primary, .. SubSpaces];

    /// The profile every member browses with.
    public Guid ProfileId => Primary.ProfileId;

    /// The member a person used last: the one whose tab was shown most recently.
    public SpaceState LastUsed => Members.MaxBy(member => member.LastShownAt)!;

    #endregion

    #region Actions - Reading

    /// The groups `spaces` holds, in order, where `pending` names the Spaces
    /// whose deletion is under way. Each group sits where its primary does.
    internal static IReadOnlyList<SpaceGroup> Of(IReadOnlyList<SpaceState> spaces, IReadOnlySet<Guid> pending) {
        var led = Led(spaces, pending);
        return [.. spaces.Where(space => space.PrimaryId is null && (!pending.Contains(space.Id) || led.ContainsKey(space.Id)))
            .Select(primary => new SpaceGroup(primary, led.GetValueOrDefault(primary.Id) ?? []))];
    }

    #endregion

    #region Actions - Rules

    /// The first rule the Spaces' groups break, in session order, or null when
    /// they break none: every Sub-Space that stays names a primary the session
    /// holds, sits in its primary's group and browses with its profile, and no
    /// two groups browse with one profile.
    internal static SessionFlaw? Flaw(IReadOnlyList<SpaceState> spaces, IReadOnlySet<Guid> pending) {
        var byId = spaces.DistinctBy(space => space.Id).ToDictionary(space => space.Id);
        Guid? leading = null;
        var profiles = new HashSet<Guid>();
        foreach (var space in spaces) {
            if (space.PrimaryId is not { } primaryId) {
                if (pending.Contains(space.Id) && !Leads(space, spaces, pending)) continue;
                leading = space.Id;
                if (!profiles.Add(space.ProfileId)) return SessionFlaw.SharedProfile;
                continue;
            }
            if (pending.Contains(space.Id)) continue;
            if (!byId.TryGetValue(primaryId, out var primary) || primary.Id == space.Id || primary.PrimaryId is not null)
                return SessionFlaw.UnknownPrimary;
            if (leading != primaryId || space.ProfileId != primary.ProfileId) return SessionFlaw.ScatteredGroup;
        }
        return null;
    }

    #endregion

    #region Actions - Repair

    /// `spaces` with every group mended, where `pending` names the Spaces
    /// whose deletion is under way, which stay exactly as they are. A Sub-Space
    /// whose primary is a Sub-Space itself joins that one's primary. The
    /// Sub-Spaces of a primary that is gone stay together, led by the first of
    /// them, which keeps their profile. A Sub-Space browses with its primary's
    /// profile. A group that took over a profile another group browses with
    /// gives it up and browses with its primary's own. Each group sits where
    /// its primary does. A Space that needs no mending is the same object.
    internal static IReadOnlyList<SpaceState> Regrouped(IReadOnlyList<SpaceState> spaces, IReadOnlySet<Guid> pending) {
        var mended = Mended(spaces, pending);
        mended = Distinct(mended, pending);
        mended = Aligned(mended, pending);
        return Gathered(mended, pending);
    }

    /// Every Sub-Space that stays names a primary the session holds, once:
    /// one whose primary is a Sub-Space joins that one's primary, and those
    /// whose primary is gone are led by the first of them.
    private static List<SpaceState> Mended(IReadOnlyList<SpaceState> spaces, IReadOnlySet<Guid> pending) {
        var byId = spaces.DistinctBy(space => space.Id).ToDictionary(space => space.Id);
        var result = spaces.ToList();
        var orphans = new Dictionary<Guid, List<int>>();
        for (var index = 0; index < result.Count; index++) {
            var space = result[index];
            if (pending.Contains(space.Id) || space.PrimaryId is not { } primaryId) continue;
            if (PrimaryOf(byId, space, primaryId) is { } primary) {
                if (primary.Id != primaryId) result[index] = space with { Membership = space.Membership! with { PrimaryId = primary.Id } };
                continue;
            }
            if (!orphans.TryGetValue(primaryId, out var members)) orphans[primaryId] = members = [];
            members.Add(index);
        }
        foreach (var members in orphans.Values) {
            var modifiedAt = members.Max(index => result[index].Membership!.ModifiedAt);
            var leader = result[members[0]];
            result[members[0]] = leader with {
                Membership = leader.Membership!.SharedProfileId is { } profile
                    ? SpaceMembership.Leading(profile, modifiedAt) : SpaceMembership.Leaving(modifiedAt)
            };
            foreach (var index in members.Skip(1))
                result[index] = result[index] with { Membership = SpaceMembership.Joining(result[members[0]], modifiedAt) };
        }
        return result;
    }

    /// The primary `space` joins when it names `primaryId`: that Space when it
    /// is a primary, its primary when it is a Sub-Space of one, or null when
    /// neither stays in the session.
    private static SpaceState? PrimaryOf(Dictionary<Guid, SpaceState> byId, SpaceState space, Guid primaryId) {
        if (!byId.TryGetValue(primaryId, out var named) || named.Id == space.Id) return null;
        if (named.PrimaryId is null) return named;
        return named.PrimaryId is { } next && next != space.Id && byId.TryGetValue(next, out var primary) && primary.PrimaryId is null
            ? primary : null;
    }

    /// No two groups browse with one profile: a primary that browses with its
    /// own profile keeps it, and of two primaries that took one over, the
    /// first keeps it while the other browses with its own again.
    private static List<SpaceState> Distinct(List<SpaceState> spaces, IReadOnlySet<Guid> pending) {
        var primaries = spaces.Where(space => space.PrimaryId is null && (!pending.Contains(space.Id) || Leads(space, spaces, pending)))
            .ToList();
        var taken = primaries.Where(primary => primary.Membership?.SharedProfileId is null).Select(primary => primary.ProfileId)
            .ToHashSet();
        var result = spaces.ToList();
        foreach (var primary in primaries.Where(primary => primary.Membership?.SharedProfileId is not null)) {
            if (taken.Add(primary.ProfileId)) continue;
            var index = result.IndexOf(primary);
            result[index] = primary with { Membership = SpaceMembership.Leaving(primary.Membership!.ModifiedAt) };
        }
        return result;
    }

    /// Every Sub-Space that stays browses with its primary's profile.
    private static List<SpaceState> Aligned(List<SpaceState> spaces, IReadOnlySet<Guid> pending) {
        var byId = spaces.DistinctBy(space => space.Id).ToDictionary(space => space.Id);
        return [.. spaces.Select(space => !pending.Contains(space.Id) && space.PrimaryId is { } primaryId
            && byId[primaryId].ProfileId is var profile && space.ProfileId != profile
                ? space with { Membership = space.Membership! with { SharedProfileId = profile } } : space)];
    }

    /// Each group's Sub-Spaces follow its primary, in their order, wherever
    /// they stood; every other Space keeps its place.
    private static IReadOnlyList<SpaceState> Gathered(List<SpaceState> spaces, IReadOnlySet<Guid> pending) {
        var led = Led(spaces, pending);
        var following = led.Values.SelectMany(members => members).Select(member => member.Id).ToHashSet();
        var result = new List<SpaceState>(spaces.Count);
        foreach (var space in spaces) {
            if (following.Contains(space.Id)) continue;
            result.Add(space);
            if (led.TryGetValue(space.Id, out var members)) result.AddRange(members);
        }
        return result.SequenceEqual(spaces) ? spaces : result;
    }

    /// The Sub-Spaces that stay, by the primary they name, in session order.
    private static Dictionary<Guid, List<SpaceState>> Led(IReadOnlyList<SpaceState> spaces, IReadOnlySet<Guid> pending) {
        var led = new Dictionary<Guid, List<SpaceState>>();
        foreach (var space in spaces) {
            if (pending.Contains(space.Id) || space.PrimaryId is not { } primaryId) continue;
            if (!led.TryGetValue(primaryId, out var members)) led[primaryId] = members = [];
            members.Add(space);
        }
        return led;
    }

    /// Whether `space` leads a Sub-Space that stays.
    private static bool Leads(SpaceState space, IReadOnlyList<SpaceState> spaces, IReadOnlySet<Guid> pending) =>
        spaces.Any(other => other.PrimaryId == space.Id && !pending.Contains(other.Id));

    #endregion
}
