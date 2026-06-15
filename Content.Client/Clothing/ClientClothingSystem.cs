using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Client.DisplacementMap;
using Content.Client.Inventory;
using Content.Shared.Clothing;
using Content.Shared.Clothing.Components;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Item;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Serialization.TypeSerializers.Implementations;
using Robust.Shared.Utility;
using static Robust.Client.GameObjects.SpriteComponent;

namespace Content.Client.Clothing;

public sealed partial class ClientClothingSystem : ClothingSystem
{
    public const string Jumpsuit = "jumpsuit";

    private static readonly Dictionary<string, string> TemporarySlotMap = new()
    {
        {"head", "HELMET"},
        {"eyes", "EYES"},
        {"ears", "EARS"},
        {"mask", "MASK"},
        {"outerClothing", "OUTERCLOTHING"},
        {Jumpsuit, "INNERCLOTHING"},
        {"neck", "NECK"},
        {"back", "BACKPACK"},
        {"belt", "BELT"},
        {"gloves", "HAND"},
        {"shoes", "FEET"},
        {"id", "IDCARD"},
        {"pocket1", "POCKET1"},
        {"pocket2", "POCKET2"},
        {"suitstorage", "SUITSTORAGE"},
        {"socks", "SOCKS"},
        {"underweart", "UNDERWEART"},
        {"underwearb", "UNDERWEARB"},
    };

    /// <summary>
    /// Render priority for slots. Lower number = rendered below (underneath) higher numbers.
    /// </summary>
    private static readonly Dictionary<string, int> SlotRenderPriority = new()
    {
        {"socks", 10},
        {"underwearb", 20},
        {"underweart", 30},
        {"jumpsuit", 100},
        {"gloves", 200},
        {"shoes", 200},
        {"outerClothing", 300},
        {"belt", 400},
        {"back", 400},
        {"neck", 400},
        {"mask", 500},
        {"eyes", 500},
        {"ears", 500},
        {"head", 500},
        {"id", 600},
        {"pocket1", 600},
        {"pocket2", 600},
        {"suitstorage", 600},
    };

    [Dependency] private IResourceCache _cache = default!;
    [Dependency] private InventorySystem _inventorySystem = default!;
    [Dependency] private DisplacementMapSystem _displacement = default!;
    [Dependency] private SpriteSystem _sprite = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ClothingComponent, GetEquipmentVisualsEvent>(OnGetVisuals);
        SubscribeLocalEvent<InventoryComponent, InventoryTemplateUpdated>(OnInventoryTemplateUpdated);
        SubscribeLocalEvent<InventoryComponent, VisualsChangedEvent>(OnVisualsChanged);
        SubscribeLocalEvent<SpriteComponent, DidUnequipEvent>(OnDidUnequip);
        SubscribeLocalEvent<InventoryComponent, AppearanceChangeEvent>(OnAppearanceUpdate);
    }

    private void OnAppearanceUpdate(EntityUid uid, InventoryComponent component, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        UpdateAllSlots(uid, component);

        if (_sprite.LayerMapTryGet((uid, args.Sprite), HumanoidVisualLayers.StencilMask, out var layer, false))
        {
            DebugTools.Assert(!args.Sprite[layer].Visible);
            _sprite.LayerSetVisible((uid, args.Sprite), layer, false);
        }
    }

    private void OnInventoryTemplateUpdated(Entity<InventoryComponent> ent, ref InventoryTemplateUpdated args)
    {
        UpdateAllSlots(ent.Owner, ent.Comp);
    }

    private void UpdateAllSlots(EntityUid uid, InventoryComponent? inventoryComponent = null)
    {
        var enumerator = _inventorySystem.GetSlotEnumerator((uid, inventoryComponent));

        // First, clear all visual layer keys
        if (TryComp(uid, out InventorySlotsComponent? inventorySlots))
        {
            foreach (var slot in inventorySlots.VisualLayerKeys.Keys.ToList())
            {
                if (inventorySlots.VisualLayerKeys.TryGetValue(slot, out var revealedLayers))
                {
                    foreach (var key in revealedLayers)
                    {
                        _sprite.RemoveLayer((uid, null), key, false);
                    }
                    revealedLayers.Clear();
                }
            }
        }

        // Collect all items with their slots and priorities
        var itemsToRender = new List<(EntityUid item, string slot, int priority)>();

        while (enumerator.NextItem(out var item, out var slot))
        {
            var priority = GetSlotRenderPriority(slot.Name);
            itemsToRender.Add((item, slot.Name, priority));
        }

        // Sort by priority (lowest first = rendered below)
        itemsToRender = itemsToRender.OrderBy(x => x.priority).ToList();

        // Render items in priority order
        if (TryComp(uid, out SpriteComponent? sprite) && TryComp(uid, out inventorySlots))
        {
            foreach (var (item, slot, _) in itemsToRender)
            {
                RenderEquipment(uid, item, slot, inventoryComponent, sprite, null, inventorySlots);
            }
        }
    }

    private void OnGetVisuals(EntityUid uid, ClothingComponent item, GetEquipmentVisualsEvent args)
    {
        if (!TryComp(args.Equipee, out InventoryComponent? inventory))
            return;

        List<PrototypeLayerData>? layers = null;

        if (inventory.SpeciesId != null)
            item.ClothingVisuals.TryGetValue($"{args.Slot}-{inventory.SpeciesId}", out layers);

        if (layers == null && !item.ClothingVisuals.TryGetValue(args.Slot, out layers))
        {
            if (!TryGetDefaultVisuals(uid, item, args.Slot, inventory.SpeciesId, out layers))
                return;
        }

        var i = 0;
        foreach (var layerData in layers)
        {
            var key = layerData.MapKeys?.FirstOrDefault();
            if (key == null)
            {
                key = $"{args.Slot}-{i}";
                i++;
            }

            item.MappedLayer = key;
            args.Layers.Add((key, layerData));
        }
    }

    private bool TryGetDefaultVisuals(EntityUid uid, ClothingComponent clothing, string slot, string? speciesId,
        [NotNullWhen(true)] out List<PrototypeLayerData>? layers)
    {
        layers = null;

        RSI? rsi = null;

        if (clothing.RsiPath != null)
            rsi = _cache.GetResource<RSIResource>(SpriteSpecifierSerializer.TextureRoot / clothing.RsiPath).RSI;
        else if (TryComp(uid, out SpriteComponent? sprite))
            rsi = sprite.BaseRSI;

        if (rsi == null)
            return false;

        var correctedSlot = slot;
        TemporarySlotMap.TryGetValue(correctedSlot, out correctedSlot);

        var state = $"equipped-{correctedSlot}";

        if (!string.IsNullOrEmpty(clothing.EquippedPrefix))
            state = $"{clothing.EquippedPrefix}-equipped-{correctedSlot}";

        if (clothing.EquippedState != null)
            state = $"{clothing.EquippedState}";

        if (speciesId != null && rsi.TryGetState($"{state}-{speciesId}", out _))
            state = $"{state}-{speciesId}";
        else if (!rsi.TryGetState(state, out _))
            return false;

        var layer = new PrototypeLayerData();
        layer.RsiPath = rsi.Path.ToString();
        layer.State = state;
        layer.Scale = clothing.Scale;
        layers = new() { layer };

        return true;
    }

    private void OnVisualsChanged(EntityUid uid, InventoryComponent component, VisualsChangedEvent args)
    {
        UpdateAllSlots(uid, component);
    }

    private void OnDidUnequip(Entity<SpriteComponent> entity, ref DidUnequipEvent args)
    {
        if (!TryComp(entity, out InventoryComponent? inventory))
            return;

        UpdateAllSlots(entity.Owner, inventory);
    }

    public void InitClothing(EntityUid uid, InventoryComponent component)
    {
        UpdateAllSlots(uid, component);
    }

    protected override void OnGotEquipped(EntityUid uid, ClothingComponent component, GotEquippedEvent args)
    {
        base.OnGotEquipped(uid, component, args);

        if (TryComp(args.EquipTarget, out InventoryComponent? inventory))
        {
            UpdateAllSlots(args.EquipTarget, inventory);
        }
    }

    private int GetSlotRenderPriority(string slot)
    {
        return SlotRenderPriority.GetValueOrDefault(slot, 500);
    }

    private void RenderEquipment(EntityUid equipee, EntityUid equipment, string slot,
        InventoryComponent? inventory = null, SpriteComponent? sprite = null, ClothingComponent? clothingComponent = null,
        InventorySlotsComponent? inventorySlots = null)
    {
        if (!Resolve(equipee, ref inventory, ref sprite, ref inventorySlots) ||
           !Resolve(equipment, ref clothingComponent, false))
        {
            return;
        }

        if (!_inventorySystem.TryGetSlot(equipee, slot, out var slotDef, inventory))
            return;

        if (!inventorySlots.VisualLayerKeys.TryGetValue(slot, out var revealedLayers))
        {
            revealedLayers = new();
            inventorySlots.VisualLayerKeys[slot] = revealedLayers;
        }

        var ev = new GetEquipmentVisualsEvent(equipee, slot);
        RaiseLocalEvent(equipment, ev);

        if (ev.Layers.Count == 0)
        {
            RaiseLocalEvent(equipment, new EquipmentVisualsUpdatedEvent(equipee, slot, revealedLayers), true);
            return;
        }

        var displacementData = inventory.Displacements.GetValueOrDefault(slot);

        var equipeeSex = CompOrNull<HumanoidProfileComponent>(equipee)?.Sex;
        if (equipeeSex != null)
        {
            switch (equipeeSex)
            {
                case Sex.Male:
                    if (inventory.MaleDisplacements.Count > 0)
                        displacementData = inventory.MaleDisplacements.GetValueOrDefault(slot);
                    break;
                case Sex.Female:
                    if (inventory.FemaleDisplacements.Count > 0)
                        displacementData = inventory.FemaleDisplacements.GetValueOrDefault(slot);
                    break;
            }
        }

        // Add new layers
        foreach (var (key, layerData) in ev.Layers)
        {
            if (!revealedLayers.Add(key))
            {
                Log.Warning($"Duplicate key for clothing visuals: {key}. Equipment: {ToPrettyString(equipment)}");
                continue;
            }

            // Add the layer at the end (append) - order is controlled by UpdateAllSlots
            var layerIndex = _sprite.AddLayer((equipee, sprite), layerData, null);
            _sprite.LayerMapSet((equipee, sprite), key, layerIndex);

            if (layerData.Color != null)
                _sprite.LayerSetColor((equipee, sprite), key, layerData.Color.Value);
            if (layerData.Scale != null)
                _sprite.LayerSetScale((equipee, sprite), key, layerData.Scale.Value);

            if (_sprite.TryGetLayer((equipee, sprite), layerIndex, out var existingLayer, true))
            {
                _sprite.LayerSetOffset(existingLayer, existingLayer.Offset + slotDef.Offset);
            }

            if (displacementData is not null)
            {
                if (layerData.State is not null && inventory.SpeciesId is not null && layerData.State.EndsWith(inventory.SpeciesId))
                    continue;

                if (_displacement.TryAddDisplacement(displacementData, (equipee, sprite), layerIndex, key, out var displacementKey))
                {
                    revealedLayers.Add(displacementKey);
                }
            }
        }

        RaiseLocalEvent(equipment, new EquipmentVisualsUpdatedEvent(equipee, slot, revealedLayers), true);
    }
}
