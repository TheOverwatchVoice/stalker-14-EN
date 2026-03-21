using Content.Shared.Actions;
using Content.Shared.Armor;
using Content.Shared.Clothing.Components;
using Content.Shared.Damage;
using Content.Shared.IdentityManagement;
using Content.Shared.IdentityManagement.Components;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Nutrition.Components;
using Content.Shared.Weapons.Reflect;
using Robust.Shared.GameStates;
using Robust.Shared.Timing;
using System.Security.Principal;

namespace Content.Shared.Clothing.EntitySystems;

public sealed class HelmetVisorSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly InventorySystem _inventorySystem = default!;
    [Dependency] private readonly ClothingSystem _clothing = default!;
    [Dependency] private readonly IdentitySystem _identity = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<HelmetVisorComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<HelmetVisorComponent, ToggleHelmetVisorEvent>(OnToggle);
        SubscribeLocalEvent<HelmetVisorComponent, GetItemActionsEvent>(OnGetActions);
    }

    private void OnInit(EntityUid uid, HelmetVisorComponent comp, ComponentInit args)
    {
        if (TryComp<ArmorComponent>(uid, out var armor))
            comp.DefaultModifiers = armor.Modifiers;

        if (TryComp<ReflectComponent>(uid, out var reflect))
            comp.DefaultReflectProb = reflect.ReflectProb;

        UpdateBlockers(uid, comp);
    }

    private void OnGetActions(EntityUid uid, HelmetVisorComponent comp, GetItemActionsEvent args)
    {
        if (!comp.IsToggleable)
            return;

        if (args.SlotFlags == null || (args.SlotFlags.Value & SlotFlags.HEAD) == 0)
            return;

        args.AddAction(ref comp.ToggleActionEntity, comp.ToggleAction);
        Dirty(uid, comp);
    }

    private void OnToggle(EntityUid uid, HelmetVisorComponent comp, ToggleHelmetVisorEvent args)
    {
        if (args.Handled || !comp.IsToggleable)
            return;

        SetUp(uid, comp, !comp.IsUp);
        args.Handled = true;
    }

    public void SetUp(EntityUid uid, HelmetVisorComponent comp, bool up, bool force = false)
    {
        if (_timing.ApplyingState)
            return;

        if (!force && !comp.IsToggleable)
            return;

        if (comp.IsUp == up)
            return;

        comp.IsUp = up;

        if (comp.ToggleActionEntity is { } action)
            _actions.SetToggled(action, comp.IsUp);

        UpdateVisuals(uid, comp);
        RaiseLocalEvent(uid, new VisorToggledEvent(uid, comp.IsUp));
        Dirty(uid, comp);
    }

    private void UpdateVisuals(EntityUid uid, HelmetVisorComponent comp)
    {
        if (comp.EquippedPrefixUp == null)
            return;

        var prefix = comp.IsUp ? comp.EquippedPrefixUp : null;
        _clothing.SetEquippedPrefix(uid, prefix);
    }

    private void UpdateBlockers(EntityUid uid, HelmetVisorComponent comp)
    {
        var block = !comp.IsUp;
        RaiseLocalEvent(uid, new VisorBlockersChangedEvent(block, block));
    }
}

public readonly record struct VisorToggledEvent(EntityUid Visor, bool IsUp);
public readonly record struct VisorBlockersChangedEvent(bool BlockIngestion, bool BlockIdentity);
public readonly record struct HelmetVisorVisualsChangedEvent(string? EquippedPrefix);

