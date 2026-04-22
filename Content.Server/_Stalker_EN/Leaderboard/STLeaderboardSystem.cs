using System.Linq;
using Content.Server._Stalker_EN.FactionRelations;
using Content.Server.Administration.Managers;
using Content.Server.CartridgeLoader;
using Content.Server.PDA;
using Content.Shared._Stalker.Bands;
using Content.Shared._Stalker_EN.CharacterRank;
using Content.Shared._Stalker_EN.FactionRelations;
using Content.Shared._Stalker_EN.Leaderboard;
using Content.Shared._Stalker_EN.Portraits;
using Content.Shared.CartridgeLoader;
using Content.Shared.Containers;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Hands;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.PDA;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.Leaderboard;

/// <summary>
/// Server-side system that manages the Stalker Leaderboard cartridge.
/// Uses BandsComponent for faction display, STFactionRelationsCartridgeSystem for relations.
/// Supports multiple characters per player (keyed by UserId + CharacterName).
/// </summary>
public sealed partial class STLeaderboardSystem : EntitySystem
{
    [Dependency] private readonly CartridgeLoaderSystem _cartridgeLoader = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IAdminManager _adminManager = default!;
    [Dependency] private readonly SharedSTFactionResolutionSystem _factionResolution = default!;
    [Dependency] private readonly STFactionRelationsCartridgeSystem _factionRelations = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IConsoleHost _consoleHost = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly PdaSystem _pda = default!;
    [Dependency] private readonly ILocalizationManager _loc = default!;

    private ISawmill _sawmill = default!;

    /// <summary>
    /// PDAs with leaderboard cartridge currently active (UI open). Receive broadcast updates.
    /// </summary>
    private readonly HashSet<EntityUid> _activeLoaders = new();

    /// <summary>
    /// Cached set of all PDAs that have a leaderboard cartridge.
    /// Avoids full entity query in BroadcastUiState (pattern from STMessenger).
    /// Stores (CartridgeUid, PdaUid) — resolve components via TryComp to avoid stale references.
    /// </summary>
    private readonly Dictionary<EntityUid, (EntityUid Cartridge, EntityUid Pda)> _leaderboardPdas = new();

    /// <summary>
    /// Cache of all known stalkers. Key includes both UserId and CharacterName.
    /// All stats (rank, band name, band icon, faction) are read live from components, not cached.
    /// When Mob is deleted, entry shows dashes for all fields except name.
    /// Entries persist across player disconnects until round restart.
    /// </summary>
    private readonly Dictionary<StalkerKey, (string Name, EntityUid? Mob)> _knownStalkers = new();

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("st.leaderboard");

        SubscribeLocalEvent<STLeaderboardCartridgeComponent, CartridgeUiReadyEvent>(OnUiReady);
        SubscribeLocalEvent<STLeaderboardCartridgeComponent, CartridgeActivatedEvent>(OnCartridgeActivated);
        SubscribeLocalEvent<STLeaderboardCartridgeComponent, CartridgeDeactivatedEvent>(OnCartridgeDeactivated);
        SubscribeLocalEvent<STLeaderboardCartridgeComponent, EntityTerminatingEvent>(OnCartridgeTerminating);
        SubscribeLocalEvent<STLeaderboardServerComponent, EntityTerminatingEvent>(OnServerTerminating);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawned);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);

        _consoleHost.RegisterCommand("leaderboard-clear",
            "Clears all entries from the stalker leaderboard.",
            "Usage: leaderboard-clear",
            LeaderboardClearCommand);

        _consoleHost.RegisterCommand("leaderboard-list",
            "Lists all entries in the stalker leaderboard.",
            "Usage: leaderboard-list",
            LeaderboardListCommand);

        _consoleHost.RegisterCommand("leaderboard-remove",
            "Removes entries matching a character name from the stalker leaderboard.",
            "Usage: leaderboard-remove <name>",
            LeaderboardRemoveCommand);
    }

    /// <summary>
    /// Returns true if the entity is a real player MobHuman.
    /// All doll/NPC prototypes use different IDs, so this strictly filters real players.
    /// </summary>
    private bool IsPlayerMob(EntityUid mob) => MetaData(mob).EntityPrototype?.ID == "MobHuman";

    /// <summary>
    /// Called when the cartridge UI is first opened.
    /// </summary>
    private void OnUiReady(EntityUid uid, STLeaderboardCartridgeComponent component, CartridgeUiReadyEvent args)
    {
        // Only send UI state if the loader is active (prevents updates during program switching)
        if (!_activeLoaders.Contains(args.Loader))
            return;

        // Call BroadcastUiState instead of SendUiState to get proper viewerSession
        BroadcastUiState();
    }

    private void OnCartridgeActivated(EntityUid uid, STLeaderboardCartridgeComponent component, ref CartridgeActivatedEvent args)
    {
        _activeLoaders.Add(args.Loader);

        // Initialize owner data when cartridge is activated
        TryInitializeLeaderboard(args.Loader);
    }

    private void OnCartridgeDeactivated(EntityUid uid, STLeaderboardCartridgeComponent component, ref CartridgeDeactivatedEvent args)
    {
        _activeLoaders.Remove(args.Loader);
    }

    private void OnCartridgeTerminating(EntityUid uid, STLeaderboardCartridgeComponent component, ref EntityTerminatingEvent args)
    {
        // The loader is the PDA entity that owns this cartridge
        if (TryComp<TransformComponent>(uid, out var xform) && xform.ParentUid.IsValid())
        {
            _activeLoaders.Remove(xform.ParentUid);
            _leaderboardPdas.Remove(xform.ParentUid);
        }
    }

    private void OnPlayerSpawned(PlayerSpawnCompleteEvent args)
    {
        if (!args.Mob.IsValid())
            return;

        var mob = args.Mob;
        var session = args.Player;

        // Only real player mobs (MobHuman) - filters out dolls, NPCs, etc.
        if (!IsPlayerMob(mob))
            return;

        if (HasComp<GhostComponent>(mob))
            return;

        // Cache PDA with leaderboard cartridge (pattern from STMessenger)
        if (TryComp<TransformComponent>(mob, out var xform))
        {
            var current = xform.ParentUid;
            while (current.IsValid())
            {
                if (TryComp<CartridgeLoaderComponent>(current, out var loader) &&
                    TryComp<STLeaderboardCartridgeComponent>(loader.ActiveProgram, out var cartridge))
                {
                    _leaderboardPdas[current] = (cartridge.Owner, current);

                    // Initialize owner data for this PDA
                    TryInitializeLeaderboard(current);
                    break;
                }

                var parentXform = CompOrNull<TransformComponent>(current);
                if (parentXform == null)
                    break;

                current = parentXform.ParentUid;
            }
        }

        // Add to leaderboard
        UpdateStalkerEntry(session);

        // Broadcast UI update to all active loaders
        BroadcastUiState();
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _knownStalkers.Clear();
    }

    /// <summary>
    /// Attempts to initialize the leaderboard on a PDA for the given loader.
    /// </summary>
    private void TryInitializeLeaderboard(EntityUid loaderUid)
    {
        if (!_cartridgeLoader.TryGetProgram<STLeaderboardCartridgeComponent>(loaderUid, out var progUid, out _))
            return;

        // Add to cache even if already initialized (needed for program switching)
        _leaderboardPdas[loaderUid] = (progUid.Value, loaderUid);

        // Ensure the server component exists
        var server = EnsureComp<STLeaderboardServerComponent>(progUid.Value);

        // Guard: OwnerCharacterName is set synchronously, so if already set, skip to avoid double-loading
        if (!string.IsNullOrEmpty(server.OwnerCharacterName))
            return;

        // Get holder from TransformComponent
        if (!TryComp<TransformComponent>(loaderUid, out var xform))
            return;

        var holderUid = xform.ParentUid;
        if (!holderUid.IsValid())
            return;

        if (!TryComp<ActorComponent>(holderUid, out var actor))
            return;

        var userId = actor.PlayerSession.UserId.UserId;
        var charName = MetaData(holderUid).EntityName;

        InitializeLeaderboardForPda(loaderUid, progUid.Value, server, userId, charName, holderUid);
    }

    /// <summary>
    /// Shared logic for initializing a leaderboard PDA for a character.
    /// </summary>
    private void InitializeLeaderboardForPda(
        EntityUid pdaUid,
        EntityUid cartridgeUid,
        STLeaderboardServerComponent server,
        Guid userId,
        string charName,
        EntityUid holderUid)
    {
        server.OwnerUserId = userId;
        server.OwnerCharacterName = charName;

        _leaderboardPdas[pdaUid] = (cartridgeUid, pdaUid);
    }

    /// <summary>
    /// Handles server component termination.
    /// </summary>
    private void OnServerTerminating(Entity<STLeaderboardServerComponent> ent, ref EntityTerminatingEvent args)
    {
        // Guard against race: only remove if this entity is still the registered PDA for this character
        if (!string.IsNullOrEmpty(ent.Comp.OwnerCharacterName))
        {
            var key = (ent.Comp.OwnerUserId, ent.Comp.OwnerCharacterName);

            if (_leaderboardPdas.TryGetValue(ent.Owner, out var existing) && existing.Pda == ent.Owner)
                _leaderboardPdas.Remove(ent.Owner);
        }

        // The loader is the PDA entity that owns this cartridge
        if (TryComp<TransformComponent>(ent, out var xform) && xform.ParentUid.IsValid())
        {
            _activeLoaders.Remove(xform.ParentUid);
            _leaderboardPdas.Remove(xform.ParentUid);
        }
    }

    /// <summary>
    /// Gets the faction name for AltBand mapping.
    /// When IsDisguised, BandStatusIcon contains the original AltBand value (e.g. "stalker")
    /// AltBand contains the original BandStatusIcon (e.g. "cn") - not usable for mapping.
    /// </summary>
    private string? GetAltBandFaction(BandsComponent bands)
    {
        var bandForMapping = bands.IsDisguised ? bands.BandStatusIcon : bands.AltBand;
        return _factionResolution.GetBandFactionName(bandForMapping);
    }

    /// <summary>
    /// Gets the faction ID for relation checks based on the player's band prototype.
    /// Maps factionId to the canonical faction names used in defaults.yml.
    /// </summary>
    private string? GetPlayerFaction(EntityUid mob)
    {
        if (!TryComp<BandsComponent>(mob, out var bands))
            return null;

        string? rawFactionId = null;

        // Get faction from BandProto Name (not FactionId) to match bandMapping
        // bandMapping uses band names (e.g. "Bandits", "Freedom") not factionIds (e.g. "Bandit")
        if (bands.BandProto.HasValue && _proto.TryIndex(bands.BandProto.Value, out STBandPrototype? bandProto))
        {
            rawFactionId = bandProto.Name;
        }
        // Fallback: resolve band name via mapping
        else if (!string.IsNullOrEmpty(bands.BandName))
        {
            rawFactionId = _factionResolution.GetBandFactionName(bands.BandName);
        }

        if (string.IsNullOrEmpty(rawFactionId))
            return null;

        // Use bandMapping from defaults.yml via _factionResolution
        return _factionResolution.GetBandFactionName(rawFactionId);
    }

    /// <summary>
    /// Updates or creates a leaderboard entry for a specific player.
    /// Entries persist across disconnects - only created once per character per round.
    /// All stats (rank, band, faction) are read live from components, not cached.
    /// When Mob is deleted, entry shows dashes for all fields.
    /// </summary>
    private void UpdateStalkerEntry(ICommonSession session)
    {
        if (session.AttachedEntity is not { } mob)
            return;

        var characterName = MetaData(mob).EntityName;
        if (string.IsNullOrEmpty(characterName))
            return;

        var key = new StalkerKey(session.UserId, characterName);

        // If entry already exists, just update mob reference
        if (_knownStalkers.ContainsKey(key))
        {
            var existing = _knownStalkers[key];
            _knownStalkers[key] = (existing.Name, mob);
            return;
        }

        // Create new entry
        _knownStalkers[key] = (characterName, mob);
    }

    /// <summary>
    /// Computes the relation type between two factions using the band→faction mapping.
    /// Resolves aliases (e.g. Bandit → Bandits) before checking relations.
    /// </summary>
    private STLeaderboardFactionRelation GetRelation(string? viewerFaction, string? targetFaction)
    {
        if (string.IsNullOrEmpty(viewerFaction) || string.IsNullOrEmpty(targetFaction))
            return STLeaderboardFactionRelation.Neutral;

        // Resolve aliases (e.g. "Bandit" → "Bandits") before checking relations
        viewerFaction = _factionRelations.ResolvePrimary(viewerFaction);
        targetFaction = _factionRelations.ResolvePrimary(targetFaction);

        if (viewerFaction == targetFaction)
            return STLeaderboardFactionRelation.Same;

        // Use the faction relations system to get the actual relation type
        var relationType = _factionRelations.GetRelation(viewerFaction, targetFaction);

        return relationType switch
        {
            STFactionRelationType.Alliance => STLeaderboardFactionRelation.Alliance,
            STFactionRelationType.Neutral => STLeaderboardFactionRelation.Neutral,
            STFactionRelationType.Hostile => STLeaderboardFactionRelation.Hostile,
            STFactionRelationType.War => STLeaderboardFactionRelation.War,
            _ => STLeaderboardFactionRelation.Neutral,
        };
    }

    /// <summary>
    /// Broadcasts personalized leaderboard state to all open cartridges.
    /// Each viewer gets colors relative to their own faction.
    /// Uses cached PDAs to avoid full entity query (pattern from STMessenger).
    /// Uses stored owner data from STLeaderboardServerComponent to avoid ActorComponent lookup.
    /// </summary>
    private void BroadcastUiState()
    {
        // Use cached PDAs instead of full entity query (pattern from STMessenger)
        foreach (var (pdaUid, (cartridgeUid, _)) in _leaderboardPdas)
        {
            if (!TryComp<STLeaderboardCartridgeComponent>(cartridgeUid, out _))
                continue;

            // Only send to active loaders (UI open)
            if (!_activeLoaders.Contains(pdaUid))
                continue;

            // Get viewer session and name from stored owner data (like STMessenger does)
            ICommonSession? viewerSession = null;
            string? viewerName = null;

            if (TryComp<STLeaderboardServerComponent>(cartridgeUid, out var server))
            {
                viewerName = server.OwnerCharacterName;
                // Try to get the actual session from the player manager
                if (_playerManager.TryGetSessionById(new NetUserId(server.OwnerUserId), out var session))
                {
                    viewerSession = session;
                }
            }

            SendUiState(pdaUid, viewerSession, viewerName);
        }
    }

    /// <summary>
    /// Sends a personalized leaderboard state via the cartridge UI.
    /// Colors are computed relative to the viewer's faction.
    /// The viewer's own entry is marked with IsMe=true for client-side pinning.
    /// </summary>
    private void SendUiState(EntityUid loaderUid, ICommonSession? viewerSession = null, string? viewerName = null)
    {
        string? viewerFaction = null;

        if (viewerSession != null)
        {
            if (viewerSession.AttachedEntity is { } viewerMob)
            {
                viewerFaction = GetPlayerFaction(viewerMob);
            }
        }

        var entries = _knownStalkers.Values
            .Select(v =>
            {
                // Check if mob is deleted
                var isMobDeleted = !v.Mob.HasValue || !v.Mob.Value.IsValid();

                // Get components (live)
                BandsComponent? bands = null;
                STCharacterRankComponent? rankComp = null;
                if (!isMobDeleted)
                {
                    TryComp(v.Mob.Value, out bands);
                    TryComp(v.Mob.Value, out rankComp);
                }

                // Get band name and icon from BandsComponent
                string? bandName = bands?.BandName;
                string? bandIcon = bands?.BandStatusIcon;

                // Get faction from Mob (live)
                string? factionId = null;
                if (!isMobDeleted)
                {
                    factionId = GetPlayerFaction(v.Mob.Value);
                }

                // Get rank from STCharacterRankComponent (live)
                string? rankName = null;
                int rankIndex = 0;
                TimeSpan accumulatedTime = TimeSpan.Zero;
                if (rankComp != null)
                {
                    accumulatedTime = rankComp.AccumulatedTime;
                    // RankName is a LocId, need to resolve it to localized string
                    rankName = _loc.GetString(rankComp.RankName);
                    rankIndex = rankComp.RankIndex;
                }

                var isMe = viewerName != null && v.Name == viewerName;

                // For deleted mobs: use nodata icon, dashes for faction and name, no relations
                string? displayName = isMobDeleted ? null : v.Name;
                string? displayBandName = isMobDeleted ? null : bandName;
                string? portraitPath = isMobDeleted ? "nodata" : null;
                bool usePatch = isMobDeleted;
                STLeaderboardFactionRelation relation = STLeaderboardFactionRelation.Neutral;

                if (!isMobDeleted)
                {
                    // Get portrait path and determine if should use patch instead
                    (portraitPath, usePatch) = GetPortraitOrPatch(v.Mob, factionId, isMe);

                    // Get display band name and relation
                    displayBandName = bandName;

                    if (bands != null)
                    {
                        // Get display band name (mapped)
                        // If viewer is the target, always show true name (mapped)
                        // Monolith is never disguised - always show true name (mapped)
                        // Factions with AltBand (Clear Sky, Duty, Freedom) show mapped AltBand name (e.g. "Loners")
                        if (isMe)
                        {
                            // Viewer always sees their true faction (mapped)
                            displayBandName = factionId;
                        }
                        else if (bands.BandProto.HasValue && _proto.TryIndex(bands.BandProto.Value, out var bandProto))
                        {
                            if (bandProto.Name.Equals("Monolith", StringComparison.OrdinalIgnoreCase))
                            {
                                displayBandName = factionId;
                            }
                            else if (!string.IsNullOrEmpty(bands.AltBand))
                            {
                                displayBandName = GetAltBandFaction(bands);
                            }
                            else
                            {
                                displayBandName = factionId;
                            }
                        }
                        else
                        {
                            displayBandName = factionId;
                        }

                        // For relations: if viewer is the target, use true faction
                        // If viewer is not the target and target has AltBand, use AltBand faction (mapped)
                        // Monolith is exception - always use true faction for relations
                        string? targetFaction;
                        if (isMe)
                        {
                            targetFaction = factionId;
                        }
                        else if (!string.IsNullOrEmpty(bands.AltBand))
                        {
                            // Check if this is Monolith - Monolith never uses AltBand for relations
                            if (bands.BandProto.HasValue && _proto.TryIndex(bands.BandProto.Value, out var relationProto))
                            {
                                if (relationProto.Name.Equals("Monolith", StringComparison.OrdinalIgnoreCase))
                                {
                                    targetFaction = factionId;
                                }
                                else
                                {
                                    targetFaction = GetAltBandFaction(bands);
                                }
                            }
                            else
                            {
                                targetFaction = GetAltBandFaction(bands);
                            }
                        }
                        else
                        {
                            targetFaction = factionId;
                        }

                        relation = GetRelation(viewerFaction, targetFaction);
                    }
                }
                // For deleted mobs, relation remains Neutral (set above)

                return new STLeaderboardEntry(
                    displayName,
                    bandName,
                    bandIcon,
                    rankIndex,
                    rankName,
                    relation,
                    IsMe: isMe,
                    AccumulatedTime: accumulatedTime,
                    PortraitPath: portraitPath,
                    UsePatchInsteadOfPortrait: usePatch,
                    displayBandName);
            })
            .OrderByDescending(e => e.RankIndex)
            .ThenByDescending(e => e.AccumulatedTime)
            .ThenBy(e => e.CharacterName)
            .ToList();

        var state = new STLeaderboardUiState(entries);
        _cartridgeLoader.UpdateCartridgeUiState(loaderUid, state);
    }

    /// <summary>
    /// Gets the portrait path for a stalker, or determines if patch should be used instead.
    /// For factions with AltBand (Clear Sky, Duty, Freedom), always use disguised portrait.
    /// Monolith is never disguised - always use true portrait.
    /// Priority: disguised portrait (for AltBand factions) > normal portrait > patch (for disguise-capable factions) > patch (fallback)
    /// </summary>
    private (string? PortraitPath, bool UsePatch) GetPortraitOrPatch(EntityUid? mob, string? factionId, bool isMe)
    {
        if (!mob.HasValue || !mob.Value.IsValid())
            return (null, true); // Offline - use patch

        // If viewer is the target, always use true portrait
        if (isMe)
        {
            if (TryComp<CharacterPortraitComponent>(mob.Value, out var myPortrait))
            {
                if (!string.IsNullOrEmpty(myPortrait.PortraitTexturePath))
                    return (myPortrait.PortraitTexturePath, false);
            }
            return (null, true);
        }

        // Check if this is a faction with AltBand (always disguised in leaderboard)
        bool shouldUseDisguisedPortrait = false;
        if (TryComp<BandsComponent>(mob.Value, out var bands))
        {
            if (bands.BandProto.HasValue && _proto.TryIndex(bands.BandProto.Value, out var bandProto))
            {
                // Monolith is never disguised
                if (!bandProto.Name.Equals("Monolith", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(bands.AltBand))
                {
                    shouldUseDisguisedPortrait = true;
                }
            }
        }

        // Try to get portrait from CharacterPortraitComponent first
        if (TryComp<CharacterPortraitComponent>(mob.Value, out var portrait))
        {
            // If faction with AltBand, always use disguised portrait
            if (shouldUseDisguisedPortrait && !string.IsNullOrEmpty(portrait.DisguisedPortraitPath))
                return (portrait.DisguisedPortraitPath, false);

            // Otherwise use normal portrait
            if (!string.IsNullOrEmpty(portrait.PortraitTexturePath))
                return (portrait.PortraitTexturePath, false);
        }

        // No portrait available - use patch
        return (null, true);
    }

    /// <summary>
    /// Clears all entries from the leaderboard (admin use).
    /// </summary>
    public void ClearLeaderboard()
    {
        _knownStalkers.Clear();
        BroadcastUiState();
    }

    /// <summary>
    /// Removes a specific stalker from the leaderboard.
    /// </summary>
    public bool RemoveStalker(StalkerKey key)
    {
        if (_knownStalkers.Remove(key))
        {
            BroadcastUiState();
            return true;
        }
        return false;
    }
}
