import Foundation

/// The groups of this window's workspace, read from the core's read model. A
/// group is a primary and the Sub-Spaces that joined its session, in order:
/// they browse with one profile, the primary keeps the group's history and
/// passwords, and its pinned tabs show in every Sub-Space unless the group
/// keeps them apart. A Space on its own is a group of one.
extension BrowserStore {
    // MARK: - Actions - Reading

    /// The primary of the group `space` belongs to: the Space whose session it
    /// joined, or the Space itself.
    func primary(of space: SpaceModel) -> SpaceModel {
        space.membership?.primaryID.flatMap(spaceModel) ?? space
    }

    /// The Spaces of the group `space` belongs to that stay, the primary
    /// first and then its Sub-Spaces in order.
    func groupMembers(of space: SpaceModel) -> [SpaceModel] {
        let primaryID = primary(of: space).id
        return spaceModels.filter {
            ($0.id == primaryID || $0.membership?.primaryID == primaryID) && !isDeleting($0.id)
        }
    }

    /// The Spaces whose passwords the group of `spaceID` reads, the one that
    /// keeps its new passwords first: the group's primary, then the Sub-Spaces,
    /// which keep what they saved before they joined.
    func credentialSpaceIDs(of spaceID: UUID) -> [UUID] {
        guard let space = spaceModel(spaceID) else { return [spaceID] }
        return groupMembers(of: space).map(\.id)
    }
}
