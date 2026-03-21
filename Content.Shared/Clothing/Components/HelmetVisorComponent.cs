// Content.Shared/_Stalker/HelmetVisor/HelmetVisorComponent.cs
using Content.Shared.Clothing.EntitySystems;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Content.Shared.Damage;

namespace Content.Shared.Clothing.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(HelmetVisorSystem))]
public sealed partial class HelmetVisorComponent : Component
{
    [DataField, AutoNetworkedField]
    public EntProtoId ToggleAction = "ToggleHelmetVisorEvent";

    [DataField, AutoNetworkedField]
    public EntityUid? ToggleActionEntity;

    [DataField, AutoNetworkedField]
    public bool IsUp;

    [DataField, AutoNetworkedField]
    public string? EquippedPrefixUp;

    [DataField, AutoNetworkedField]
    public bool IsToggleable = true;

    [DataField]
    public DamageModifierSet? VisorUpModifiers;

    public DamageModifierSet? DefaultModifiers;

    [DataField]
    public float? VisorUpReflectProb;

    public float DefaultReflectProb;
}
