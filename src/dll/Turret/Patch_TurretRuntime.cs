using HarmonyLib;

// Laufzeitverhalten unserer Turrets, das vanilla nicht liefert:
//   (1) der Zaehler fuer gleichzeitig aktive Turrets darf uns nicht abschalten,
//   (2) ein leergeschossenes Turret verschwindet, statt nur auszugehen,
//   (3) die Lebenszeit laeuft ab.
//
// (3) rechnet `TurretLifetime`; dort steht auch, warum die Uhr in der ItemValue-Metadata liegt
// und nicht - wie zuerst gebaut - in einem Buff.
[HarmonyPatch(typeof(EntityTurret), "OnUpdateEntity")]
public static class Patch_TurretRuntime
{
    // Tag an der entity_class, an dem wir unsere Turrets erkennen. Vanilla-Turrets und die
    // anderer Mods bleiben damit voellig unberuehrt - dieser Patch laeuft ueber JEDES
    // EntityTurret im Spiel.
    private const string AgentTurretTag = "agentTurret";

    private static FastTags<TagGroup.Global> agentTag;
    private static bool agentTagReady;

    public static bool IsAgentTurret(EntityTurret turret)
    {
        if (turret == null) return false;
        var cls = turret.EntityClass;
        if (cls == null) return false;
        if (!agentTagReady)
        {
            agentTag = FastTags<TagGroup.Global>.Parse(AgentTurretTag);
            agentTagReady = true;
        }
        return cls.Tags.Test_AnySet(agentTag);
    }

    static void Postfix(EntityTurret __instance)
    {
        if (!IsAgentTurret(__instance)) return;

        // ---- (0) Dauerflamme -----------------------------------------------------------
        // Vor der Server-Pruefung und vor dem IsDead-Ausstieg: die Flamme ist ein sichtbarer
        // Effekt, und sie muss auch dann ausgehen, wenn das Turret gerade stirbt.
        TurretFlameJet.Tick(__instance);

        if (__instance.IsDead()) return;

        // ---- (1) Den Aktivzaehler dauerhaft aushebeln ----------------------------------
        // `OnUpdateEntity` rechnet pro Frame:
        //     maxCount = GetValue(JunkTurretActiveCount, OriginalItemValue, base 1, Owner)
        //     IsOn &= (aktive <= maxCount) || naeher_als_der_andere || ForceOn      (IL_0365..)
        // und setzt ForceOn danach selbst wieder auf false - es ist als EIN-FRAME-Gnade fuer
        // frisch platzierte Turrets gedacht.
        //
        // ⚠ Ohne diese Zeile begrenzt uns ein FREMDER Wert: `perkTurrets` setzt
        // `JunkTurretActiveCount` per `base_set 0` OHNE `tags`-Attribut (vanilla
        // progression.xml:2999) - der Effekt trifft also jedes Item, auch unsere Kern-Items.
        // Ein Spieler mit dem Turret-Perk bekaeme dadurch WENIGER gleichzeitige Agent-Turrets
        // als einer ohne. Da unsere Turrets ohnehin eine harte Lebenszeit haben und einmalig
        // bezahlt sind, ist der Zaehler das falsche Balancing-Werkzeug; die Grenze ist, wie
        // viele Boxen man dabei hat.
        __instance.ForceOn = true;

        // ---- (2) Leergeschossen = abgebaut ---------------------------------------------
        // Entfernen ist Serversache. Auf dem Client wuerde MarkToUnload nur die lokale Kopie
        // wegnehmen und der naechste Zustandsabgleich holt sie zurueck.
        if (!ConnectionManager.Instance.IsServer) return;

        // AmmoCount ist ein Alias auf `OriginalItemValue.Meta`, und `MiniTurretFireController.Fire`
        // zaehlt es selbst herunter (IL_1561..IL_1572) - der Verbrauch ist also vanilla.
        // Vanilla LAESST das leere Turret aber stehen und schaltet es nur ab
        // (`IsOn &= Meta > 0`, IL_01a9..IL_01c4). Fuer einen Einwegartikel ist das die falsche
        // Endlage: es bliebe ein totes Requisit in der Landschaft stehen.
        if (__instance.AmmoCount <= 0)
        {
            TurretRetire.Retire(__instance, "Magazin leer");
            return;
        }

        // ---- (3) Lebenszeit ------------------------------------------------------------
        if (TurretLifetime.Tick(__instance))
            TurretRetire.Retire(__instance, "Lebenszeit abgelaufen");
    }
}
