using System.Linq;
using Content.Shared._Stalker.Bands;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Shared._Stalker_EN.Portraits;

/// <summary>
/// Resolves character portraits for entities.
/// If PortraitTexturePath is not set, picks a random texture from available portraits for the entity's job/band.
/// Also resolves disguise portraits for factions that can disguise.
/// </summary>
public sealed class CharacterPortraitSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = Logger.GetSawmill("st.portrait.system");
        SubscribeLocalEvent<CharacterPortraitComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<CharacterPortraitComponent, ComponentAdd>(OnComponentAdd);
    }

    private void OnMapInit(EntityUid uid, CharacterPortraitComponent comp, MapInitEvent args)
    {
        ResolvePortrait(uid, comp);
    }

    private void OnComponentAdd(EntityUid uid, CharacterPortraitComponent comp, ComponentAdd args)
    {
        ResolvePortrait(uid, comp);
    }

    /// <summary>
    /// Resolve portrait into texture path.
    /// If PortraitTexturePath is already set, validates it.
    /// If empty, picks a random texture from portraits matching the entity's job/band.
    /// Can be called via VV to re-resolve after manual changes.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public void ResolvePortrait(EntityUid uid, CharacterPortraitComponent comp)
    {
        // If texture path is already set (from player profile), validate it
        if (!string.IsNullOrEmpty(comp.PortraitTexturePath))
        {
            // Convert string to ResPath for validation
            var currentPath = new ResPath(comp.PortraitTexturePath);

            // Check if the texture path exists in any portrait prototype
            // Support both old full paths and new relative paths
            var textureExists = _protoManager.EnumeratePrototypes<CharacterPortraitPrototype>()
                .Any(p => p.Textures.Any(t => t == currentPath || p.GetFullPath(t) == currentPath));

            if (!textureExists)
            {
                _sawmill.Warning($"Portrait texture path not found in any prototype: {comp.PortraitTexturePath}");
                comp.PortraitTexturePath = string.Empty;
                Dirty(uid, comp);
            }
            else
            {
                // Valid path - keep as-is (supports both relative and absolute paths)
                Dirty(uid, comp);
                ResolveDisguisePortrait(uid, comp);
                return;
            }
        }

        // No valid texture path set — pick random from matching portraits for MAIN portrait
        string? targetBandId = null;
        string? targetJobId = null;

        // First, check if portraitJobId is explicitly set in CharacterPortraitComponent (overrides hierarchy)
        if (!string.IsNullOrEmpty(comp.PortraitJobId))
        {
            targetJobId = comp.PortraitJobId;
        }
        else
        {
            // Otherwise, get band and job from BandsComponent (for NPCs)
            if (TryComp<BandsComponent>(uid, out var bands))
            {
                targetBandId = bands.BandProto?.Id ?? bands.BandName;

                if (bands.BandProto.HasValue)
                {
                    if (_protoManager.TryIndex<STBandPrototype>(bands.BandProto.Value, out var bandProto) &&
                        bandProto.Hierarchy.TryGetValue(bands.BandRankId, out var jobProtoId))
                    {
                        targetJobId = jobProtoId.Id;
                    }
                }
            }
        }

        // Find all matching portraits
        var matches = _protoManager.EnumeratePrototypes<CharacterPortraitPrototype>()
            .Where(p =>
            {
                // Must match band if we have one
                if (!string.IsNullOrEmpty(targetBandId) && p.BandId != targetBandId)
                    return false;

                // If we have a specific job (from portraitJobId or hierarchy), match strictly
                if (!string.IsNullOrEmpty(targetJobId))
                {
                    return p.JobId == targetJobId;
                }
                else
                {
                    return string.IsNullOrEmpty(p.JobId);
                }
            })
            .ToList();

        if (matches.Count > 0)
        {
            // Pick random portrait, then random texture from it
            var chosenProto = matches[_random.Next(matches.Count)];
            var texturePath = PickRandomTexture(chosenProto.Textures);
            comp.PortraitTexturePath = texturePath.ToString();
            Dirty(uid, comp);
        }
        else
        {
            _sawmill.Warning($"No matching portrait prototypes found for band: {targetBandId}, job: {targetJobId}");
        }

        // Resolve Disguise Portrait Path (for Clear Sky disguise)
        // Always resolve this for NPCs (who don't have profiles)
        ResolveDisguisePortrait(uid, comp);
    }

    /// <summary>
    /// Resolves the disguise portrait path randomly from target faction portraits
    /// if the entity belongs to a faction capable of disguise.
    /// Can be called via VV to re-resolve after manual changes.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    private void ResolveDisguisePortrait(EntityUid uid, CharacterPortraitComponent comp)
    {
        // Check if this band can disguise using AltBand and CanChange from BandsComponent
        var canDisguise = false;
        var targetJobId = (string?)null;

        if (TryComp<BandsComponent>(uid, out var bands))
        {
            canDisguise = bands.AltBand != null && bands.CanChange;

            if (bands.BandProto.HasValue)
            {
                if (_protoManager.TryIndex<STBandPrototype>(bands.BandProto.Value, out var bandProto))
                {
                    targetJobId = bandProto.DisguiseTargetJobId?.ToString();
                }
            }
        }

        if (!canDisguise || string.IsNullOrEmpty(targetJobId))
        {
            // If can't disguise or no target job, ensure IsDisguised is false
            if (comp.IsDisguised)
            {
                comp.IsDisguised = false;
                Dirty(uid, comp);
            }
            return;
        }

        // Set IsDisguised to true for factions that can disguise
        if (!comp.IsDisguised)
        {
            comp.IsDisguised = true;
            Dirty(uid, comp);
        }

        // Find portraits for the target job
        var targetPortraits = _protoManager.EnumeratePrototypes<CharacterPortraitPrototype>()
            .Where(p => p.JobId == targetJobId)
            .ToList();

        if (targetPortraits.Count > 0)
        {
            // If manually set, use it. If empty, pick random.
            // (For NPCs this will always be random)
            if (string.IsNullOrEmpty(comp.DisguisedPortraitPath))
            {
                var chosenProto = targetPortraits[_random.Next(targetPortraits.Count)];
                comp.DisguisedPortraitPath = PickRandomTexture(chosenProto.Textures).ToString();
                Dirty(uid, comp);
            }
        }
    }

    /// <summary>
    /// Picks a random texture from a list of texture paths.
    /// Returns empty ResPath if list is empty and logs a warning.
    /// Returns full path with prefix for relative paths.
    /// </summary>
    private ResPath PickRandomTexture(List<ResPath> texturePaths)
    {
        if (texturePaths.Count == 0)
        {
            _sawmill.Warning("Attempted to pick random texture from empty list");
            return ResPath.Empty;
        }

        var randomPath = texturePaths[_random.Next(texturePaths.Count)];

        // Get a prototype to use its GetFullPath method
        var firstProto = _protoManager.EnumeratePrototypes<CharacterPortraitPrototype>().FirstOrDefault();
        if (firstProto != null)
        {
            return firstProto.GetFullPath(randomPath);
        }

        // Fallback: if no prototype available, return as-is (shouldn't happen in normal operation)
        return randomPath;
    }
}
