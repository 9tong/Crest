import SwiftUI

/// The Settings pane list: a search field over the platform's own sidebar list,
/// grouped the way the macOS catalog orders it, with the Spaces between the
/// last two groups.
struct BrowserSettingsSidebar: View {
    @Environment(\.locale) private var locale
    @State private var standaloneScroll = BrowserNativeScrollState()
    @State private var pendingDeletion: BrowserSpaceDeletionRequest?
    @State private var pendingGroupChange: BrowserSpaceGroupChange?
    @State private var hoveredItem: BrowserSettingsSidebarItem?
    @Bindable var tabState: BrowserSettingsTabState
    let browser: BrowserStore
    let spaceAccess: BrowserSpaceAccessController
    let dataDeleter: any BrowserSpaceDataDeleting
    let openSpace: BrowserSettingsOpenSpaceAction
    let addSpace: () -> Void
    @FocusState private var isSearchFocused: Bool

    private var navigation: BrowserSettingsNavigationState { tabState.navigation }
    private var state: CoreState { browser.core.state }

    var body: some View {
        VStack(spacing: 0) {
            BrowserSettingsBuildHeader()
            searchField
            List(selection: selection) {
                if let first = visibleGroups.first {
                    destinationSection(first)
                }
                spacesSection
                ForEach(Array(visibleGroups.dropFirst().enumerated()), id: \.offset) { _, group in
                    destinationSection(group)
                }
            }
            .listStyle(.sidebar)
            .scrollContentBackground(.hidden)
            .browserNativeScrollState(tabState.sidebarScroll)
        }
        .background(BrowserSettingsCanvas.background)
        .browserSpaceDeletionConfirmation(
            $pendingDeletion, browser: browser, spaceAccess: spaceAccess, dataDeleter: dataDeleter
        )
        .browserSpaceGroupChangeConfirmation($pendingGroupChange, browser: browser, spaceAccess: spaceAccess)
        .onChange(of: navigation.query.isEmpty) { _, isEmpty in
            if !isEmpty { tabState.isArrangingSpaces = false }
        }
    }

    private var searchField: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass").foregroundStyle(.secondary).accessibilityHidden(true)
            TextField("Search settings", text: $tabState.navigation.searchText)
                .textFieldStyle(.plain).focused($isSearchFocused)
            if !navigation.searchText.isEmpty {
                Button("Clear search", systemImage: "xmark.circle.fill") { tabState.navigation.searchText = "" }
                    .labelStyle(.iconOnly).buttonStyle(.plain).foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 8)
        .frame(height: 28)
        .background(.quaternary.opacity(0.6), in: .rect(cornerRadius: 7))
        .padding(.horizontal, 10)
        .padding(.top, 12)
        .padding(.bottom, 4)
    }

    // MARK: - Destinations

    private func destinationSection(_ group: [BrowserSettingsDestination]) -> some View {
        Section {
            ForEach(group) { destination in
                Label {
                    Text(destination.navigationTitle)
                } icon: {
                    Image(systemName: destination.symbol)
                        .foregroundStyle(destination.sidebarTint)
                }
                .settingsSidebarRow(.destination(destination), hovered: $hoveredItem, selection: selection)
                .accessibilityIdentifier("settings-\(destination.name)")
            }
        }
    }

    /// The catalog's groups, keeping only the destinations the search shows.
    private var visibleGroups: [[BrowserSettingsDestination]] {
        let visible = Set(navigation.visibleDestinations(locale: locale, in: state))
        return BrowserPlatformSettingsDestinationCatalog.groups
            .map { group in group.filter(visible.contains) }
    }

    // MARK: - Spaces

    @ViewBuilder
    private var spacesSection: some View {
        let spaces = visibleSpaces
        if !spaces.isEmpty || navigation.query.isEmpty {
            Section {
                ForEach(spaces) { space in
                    spaceRow(space)
                }
                .onMove { source, destination in
                    browser.moveSpaces(from: source, to: destination)
                }
            } header: {
                spacesHeader
                    // A Sub-Space dropped on the list's heading leaves its group.
                    .dropDestination(for: String.self) { items, _ in
                        requestSeparation(of: items)
                    }
            }
        }
    }

    private var spacesHeader: some View {
        HStack(spacing: BrowserSettingsSidebarMetrics.headerButtonSpacing) {
            Text("Spaces")
            Spacer(minLength: 4)
            if navigation.query.isEmpty {
                let arranging = tabState.isArrangingSpaces
                Button {
                    tabState.isArrangingSpaces.toggle()
                } label: {
                    Image(systemName: arranging ? "checkmark" : "pencil")
                        .foregroundStyle(arranging ? AnyShapeStyle(.tint) : AnyShapeStyle(.secondary))
                }
                .help(arranging ? "Done" : "Rearrange Spaces")
                .accessibilityLabel(arranging ? "Done" : "Rearrange Spaces")
                .accessibilityIdentifier("settings-spaces-arrange")
                .disabled(browser.spaceModels.count < 2 && !arranging)

                Button(action: addSpace) {
                    Image(systemName: "plus").foregroundStyle(.secondary)
                }
                .help("Add Space")
                .accessibilityLabel("Add Space")
                .accessibilityIdentifier("settings-spaces-add")
            }
        }
        .buttonStyle(CrestChromeButtonStyle(controlSize: BrowserSettingsSidebarMetrics.headerButtonSize))
        .font(.system(size: 12, weight: .medium))
        .padding(.trailing, BrowserSettingsSidebarMetrics.headerTrailingInset)
        .accessibilityElement(children: .contain)
    }

    private func spaceRow(_ space: SpaceModel) -> some View {
        HStack(spacing: 6) {
            // A Sub-Space sits under its primary, as a tab sits in a folder.
            if browser.isSubSpace(space) {
                Color.clear.frame(width: BrowserSettingsSidebarMetrics.subSpaceIndent, height: 1)
                    .accessibilityHidden(true)
            }
            Label {
                Text(space.settings.name.isEmpty ? String(localized: "Untitled Space") : space.settings.name)
                    .lineLimit(1)
            } icon: {
                BrowserSpaceIdentityIcon(space: space, size: 18)
            }
            Spacer(minLength: 4)
            if tabState.isArrangingSpaces {
                Image(systemName: "line.3.horizontal")
                    .foregroundStyle(.secondary)
                    .accessibilityLabel("Drag to reorder")
            } else {
                if space.settings.requiresAuthentication {
                    Image(systemName: "lock.fill")
                        .imageScale(.small)
                        .foregroundStyle(.secondary)
                        .accessibilityLabel("Locked")
                }
                if navigation.selection == .space(space.id) {
                    Menu {
                        spaceMenu(space)
                    } label: {
                        Image(systemName: "ellipsis")
                    }
                    .menuStyle(.borderlessButton)
                    .menuIndicator(.hidden)
                    .fixedSize()
                    .help("Space actions")
                    .accessibilityLabel("Space actions")
                }
            }
        }
        .settingsSidebarRow(.space(space.id), hovered: $hoveredItem, selection: selection)
        .moveDisabled(!tabState.isArrangingSpaces)
        .contextMenu { spaceMenu(space) }
        .modifier(
            BrowserSettingsSpaceGroupDrag(space: space, isArranging: tabState.isArrangingSpaces) { items in
                requestJoin(of: items, into: space)
            }
        )
        .accessibilityIdentifier("settings-space-\(space.id)")
    }

    /// Asks to make the Space a dragged row names a Sub-Space of `group`'s
    /// group. Answers whether the drop named one that may join it.
    private func requestJoin(of items: [String], into group: SpaceModel) -> Bool {
        guard let space = draggedSpace(items), space !== group,
            let change = BrowserSpaceGroupChange.joining(space, into: group, in: browser)
        else { return false }
        pendingGroupChange = change
        return true
    }

    /// Asks to separate the Sub-Space a dragged row names from its group.
    private func requestSeparation(of items: [String]) -> Bool {
        guard let space = draggedSpace(items), let change = BrowserSpaceGroupChange.separating(space, in: browser)
        else { return false }
        pendingGroupChange = change
        return true
    }

    /// The Space of this workspace a dragged Settings row names.
    private func draggedSpace(_ items: [String]) -> SpaceModel? {
        guard items.count == 1, let id = items.first.flatMap(UUID.init(uuidString:)), !browser.isDeleting(id) else {
            return nil
        }
        return browser.spaceModel(id)
    }

    private func spaceMenu(_ space: SpaceModel) -> some View {
        BrowserSettingsSpaceMenu(
            browser: browser, space: space, spaceAccess: spaceAccess, openSpace: openSpace,
            requestDeletion: { pendingDeletion = $0 }, requestGroupChange: { pendingGroupChange = $0 })
    }

    /// Every Space while arranging; otherwise the Spaces the search shows.
    private var visibleSpaces: [SpaceModel] {
        let spaces = browser.spaceModels.filter { !browser.isDeleting($0.id) }
        guard !tabState.isArrangingSpaces else { return spaces }
        let tab = navigation.matchingSpaceTab(locale: locale, in: state)
        return spaces.filter { navigation.lists($0, spaceTab: tab) }
    }

    // MARK: - Selection

    /// The list's selection. A page without a row selects the row it lives
    /// under; clearing the selection is ignored, so a pane is always shown.
    private var selection: Binding<BrowserSettingsSidebarItem?> {
        Binding {
            navigation.selection.row
        } set: { item in
            item?.select(on: tabState) { id in
                openSpace(id, tab: navigation.matchingSpaceTab(locale: locale, in: state))
            }
        }
    }
}

extension View {
    /// A sidebar row for `item`, wearing Crest's hover surface while the
    /// pointer is over it unless it is the selected row.
    fileprivate func settingsSidebarRow(
        _ item: BrowserSettingsSidebarItem,
        hovered: Binding<BrowserSettingsSidebarItem?>,
        selection: Binding<BrowserSettingsSidebarItem?>
    ) -> some View {
        let isHovered = hovered.wrappedValue == item && selection.wrappedValue != item
        return
            self
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(.rect)
            .onHover { inside in
                if inside {
                    hovered.wrappedValue = item
                } else if hovered.wrappedValue == item {
                    hovered.wrappedValue = nil
                }
            }
            .listRowBackground(
                Color.clear
                    .crestInteractiveSurface(isSelected: false, isHovering: isHovered)
                    .padding(.horizontal, BrowserSettingsSidebarMetrics.rowHighlightInset)
            )
            .tag(item)
    }
}

private enum BrowserSettingsSidebarMetrics {
    /// Matches the inset of the sidebar's own selection highlight.
    static let rowHighlightInset: CGFloat = 10
    static let headerButtonSpacing: CGFloat = 4
    /// Lines the header's buttons up with the trailing glyphs of the rows.
    static let headerTrailingInset: CGFloat = 6
    static let headerButtonSize = CGSize(width: 22, height: 22)
    /// How far a Sub-Space's row sits in from its primary's.
    static let subSpaceIndent: CGFloat = 14
}

/// Lets a Space's Settings row be dragged onto another Space's row, which
/// asks to make it a Sub-Space there, or onto the Spaces heading, which asks
/// to separate it. While the person arranges Spaces, rows reorder instead.
private struct BrowserSettingsSpaceGroupDrag: ViewModifier {
    let space: SpaceModel
    let isArranging: Bool
    let drop: ([String]) -> Bool

    func body(content: Content) -> some View {
        if isArranging {
            content
        } else {
            content
                .draggable(space.id.uuidString)
                .dropDestination(for: String.self) { items, _ in drop(items) }
        }
    }
}
