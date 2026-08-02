using System.Collections.Generic;
using UnityEngine;

// Was eine geworfene Turret-Box beim Aufschlag aufbauen soll.
//
// Die Angaben stehen als gewoehnliche <property> AM WURFITEM, nicht in dieser Datei. Damit ist
// ein weiteres Turret reines XML - kein Rebuild, keine Liste in C#, die man zu pflegen vergisst.
//
// Dass beliebige Properties am Item ueberhaupt ankommen, ist geprueft und kein Zufall:
// `ItemClassesFromXml.parseItem` haengt ALLE <property>-Kinder ueber
// `DynamicProperties.Add(XElement, bool)` (IL_0059) in `ItemClass.Properties` - es gibt keine
// Whitelist bekannter Namen. Vanilla selbst nutzt das (`TintColor` an einem Item wird von
// `EntityTurret.OnUpdateEntity` generisch aus `Properties.Values` gelesen).
public sealed class TurretSpec
{
    // Die entity_class, die aufgebaut wird (muss Class=EntityTurret sein).
    public string EntityClass;
    // Das Kern-Item, das als EntityTurret.OriginalItemValue dient.
    //
    // ⚠ WARUM EIN ZWEITES ITEM: Ein Turret zieht Schaden, Reichweite, Streuung und Feuerrate
    // NICHT aus der entity_class, sondern aus diesem ItemValue - und `ItemValue.ModifyValue`
    // holt die Munitionswirkung ueber
    //     ItemClass.Actions[0] as ItemActionRanged -> MagazineItemNames[SelectedAmmoTypeIndex]
    // (IL_003c..IL_00a5). Das Kern-Item braucht deshalb zwingend ein `Action0` der Klasse
    // `Ranged`. Die Wurfbox hat dort `ThrowAway` - beides in einem Item geht nicht.
    public string CoreItem;
    // Index in die `Magazine_items` des Kern-Items. So teilen sich HP- und AP-Variante
    // dasselbe Kern-Item und unterscheiden sich nur in dieser Zahl.
    public int AmmoIndex;
    // Schuss, bis das Turret verschwindet. 0 = das Magazin des Kern-Items nehmen
    // (ItemClass.GetInitialMetadata).
    public int AmmoCount;
    // Sekunden, bis das Turret verschwindet, auch wenn noch Munition da ist.
    public float Lifetime;

    private static readonly Dictionary<int, TurretSpec> cache = new Dictionary<int, TurretSpec>();
    private static readonly Dictionary<int, bool> noSpec = new Dictionary<int, bool>();

    // Liefert null, wenn das Item keine Turret-Box ist. Das ist der Normalfall: durch
    // `GameManager.ExplosionServer` laeuft jede Granate des Spiels.
    public static TurretSpec For(ItemClass item)
    {
        if (item == null) return null;

        int id = item.Id;
        if (noSpec.ContainsKey(id)) return null;
        TurretSpec hit;
        if (cache.TryGetValue(id, out hit)) return hit;

        var values = item.Properties != null ? item.Properties.Values : null;
        string entityClass;
        if (values == null || !values.TryGetValue("TurretEntity", out entityClass)
            || string.IsNullOrEmpty(entityClass))
        {
            noSpec[id] = true;
            return null;
        }

        string coreItem;
        values.TryGetValue("TurretCoreItem", out coreItem);
        if (string.IsNullOrEmpty(coreItem))
        {
            // Halblaut scheitern waere hier falsch: eine Box mit TurretEntity aber ohne
            // TurretCoreItem ist ein XML-Fehler, kein gueltiger Zustand - und ohne
            // OriginalItemValue wuerde `EntityTurret.OnUpdateEntity` sofort eine NRE werfen
            // (IL_00d3 dereferenziert es bedingungslos).
            Debug.LogWarning("[AgentArmory] Turret-Box '" + item.GetItemName()
                + "' hat TurretEntity, aber kein TurretCoreItem - wird ignoriert.");
            noSpec[id] = true;
            return null;
        }

        var spec = new TurretSpec
        {
            EntityClass = entityClass,
            CoreItem    = coreItem,
            AmmoIndex   = ReadInt(values, "TurretAmmoIndex", 0),
            AmmoCount   = ReadInt(values, "TurretAmmoCount", 0),
            Lifetime    = ReadFloat(values, "TurretLifetime", 90f),
        };
        cache[id] = spec;
        return spec;
    }

    private static int ReadInt(Dictionary<string, string> values, string key, int fallback)
    {
        string raw;
        if (!values.TryGetValue(key, out raw)) return fallback;
        int parsed;
        return int.TryParse(raw, out parsed) ? parsed : fallback;
    }

    private static float ReadFloat(Dictionary<string, string> values, string key, float fallback)
    {
        string raw;
        if (!values.TryGetValue(key, out raw)) return fallback;
        // StringParsers ist das, was das Spiel selbst benutzt - invariant, also unabhaengig
        // von der Systemsprache. `float.TryParse` wuerde auf einem deutschen Windows an
        // "0.5" scheitern und daraus 5 machen.
        float parsed;
        return StringParsers.TryParseFloat(raw, out parsed) ? parsed : fallback;
    }
}
