using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Das Schockturret haelt eine MENGE in Kontrolle statt ein Ziel in Dauerbeschuss.
//
// Wunsch aus dem Spielbericht vom 2026-08-03: "Das koennte mehr ein Crowd Control Turret werden,
// wo der Schaden irrelevant ist. Was ist, wenn wir das Turret so machen, dass es seltener schiesst
// und Round Robin die naechstgelegenen Zombies durchschaltet?"
//
// ⚠ DIE UMSETZUNG IST BEWUSST WINZIG, UND DAS IST DER GANZE WITZ. Naheliegend waere eine eigene
// Zielsuche: Kandidaten sammeln, sortieren, Sichtlinie pruefen, Yaw und Pitch setzen. Das ist alles
// nicht noetig, denn vanilla tut es schon - man muss ihm nur sagen, welche Ziele erledigt sind.
//
//   `MiniTurretFireController.Update` (IL_00D9..IL_0110), Serverzweig:
//       if (!hasTarget)                              findTarget();
//       else if (shouldIgnoreTarget(currentEntityTarget)) { currentEntityTarget = null;
//                                                          entityTurret.TargetEntityId = -1; }
//
// Das laeuft JEDES FRAME. Und `findTarget` ruft `shouldIgnoreTarget` als einzigen Filter fuer jeden
// Kandidaten seiner nach Entfernung sortierten Liste auf (IL_00C7), bevor es Sichtlinie und
// Ausrichtung prueft. Wer also einen frisch geschockten Zombie als "ignorieren" meldet, bekommt
// zwei Dinge geschenkt:
//
//   1. Das aktuelle Ziel wird im naechsten Frame fallen gelassen, sobald der Schock sitzt.
//   2. `findTarget` nimmt daraufhin den NAECHSTGELEGENEN noch nicht geschockten Zombie - mit
//      vanillas eigener Sichtpruefung (`canHitEntity` am Ende von `shouldIgnoreTarget`) und
//      vanillas eigener Zielfuehrung.
//
// Das ist das gewuenschte Round Robin, nach Entfernung geordnet, in einem Postfix mit vier Zeilen
// Wirkung. Ein Nachbau haette dieselbe Reihenfolge erzeugt und dabei drei Dinge selbst richtig
// machen muessen, die vanilla schon richtig macht.
//
// ⚠ WARUM DIE FEUERRATE TROTZ DES WUNSCHES NICHT SINKT. "Seltener schiessen" klingt richtig, macht
// es aber schlechter. `fireRateTimer` laeuft weiter, solange es KEIN Ziel gibt, und wird nur beim
// Schuss zurueckgesetzt (IL_0221 gegen IL_0376). Nach jedem Zielwechsel ist der Zaehler also schon
// voll und der erste Schuss faellt im selben Frame - mitten in der Drehung, wo er wahrscheinlich
// vorbeigeht, denn vanilla wartet das Einschwenken nicht ab (IL_0283 setzt Yaw und Pitch und
// schiesst weiter, `IsTurning` steuert nur den Ton). Eine HOHE Kadenz liefert danach schnell den
// zweiten und dritten Versuch, bis einer sitzt; eine niedrige laesst es beim Fehlschuss und wartet.
// Die Verschwendung kam nicht von der Kadenz, sondern davon, dass ueberhaupt nicht gewechselt
// wurde. Gegen die Drehzeit hilft `TurnSpeed`, und der steht an der entity_class.
//
// Nebenwirkung, die genau richtig ist: sind alle Zombies in Reichweite geschockt, hat das Turret
// kein Ziel, schiesst nicht und verbraucht keine Munition (`Update` IL_0233: der ganze Feuerblock
// haengt an `hasTarget`). Es feuert von selbst nur so oft, wie es etwas zu markieren gibt.
[HarmonyPatch(typeof(MiniTurretFireController), "shouldIgnoreTarget")]
public static class Patch_TurretShockRotation
{
    // Diagnoseausgabe. VOR DEM RELEASE AUF false.
    private const bool DBG = false;

    // An der entity_class, damit die Mechanik nicht am Turretnamen haengt.
    private const string PropRotate = "AgentShockRotateTargets";

    // Beide vanilla. `buffShockImmune` setzt vanilla selbst, wenn ein Schock auslaeuft (5 s,
    // `BuffResistance base_set 2`, von `HasImmunity` auf 0..1 geklemmt = volle Immunitaet). Ein
    // Ziel in diesem Fenster zu beschiessen waere reine Munitionsverschwendung: der Buff wuerde
    // abgewiesen, der Schock also nicht sitzen. Deshalb steht es hier gleichberechtigt neben
    // `buffShocked` - "erledigt" heisst "geschockt ODER gerade nicht schockbar".
    private const string BuffShocked = "buffShocked";
    private const string BuffImmune  = "buffShockImmune";

    private static FieldInfo fTurret;
    private static readonly Dictionary<string, bool> configuredClasses = new Dictionary<string, bool>();

    // Nur fuer Turretsorten, die das ausdruecklich wollen. Ohne diese Schranke wuerden auch Gun-,
    // MG- und Flammenturret geschockte Ziele auslassen - harmlos, aber nicht gewollt.
    private static bool WantsRotation(EntityTurret turret)
    {
        var cls = turret != null ? turret.EntityClass : null;
        if (cls == null) return false;

        string key = cls.entityClassName;
        bool hit;
        if (key != null && configuredClasses.TryGetValue(key, out hit)) return hit;

        var values = cls.Properties != null ? cls.Properties.Values : null;
        string raw;
        hit = values != null && values.TryGetValue(PropRotate, out raw)
              && !string.IsNullOrEmpty(raw) && raw != "0" && raw.ToLower() != "false";
        if (key != null) configuredClasses[key] = hit;
        return hit;
    }

    static void Postfix(MiniTurretFireController __instance, Entity _target, ref bool __result)
    {
        // Vanilla lehnt das Ziel schon ab (tot, hinter dem Rohr, Verbuendeter, keine Sichtlinie).
        // Dann gibt es nichts hinzuzufuegen, und vor allem nichts zurueckzunehmen.
        if (__result || __instance == null || _target == null) return;

        if (fTurret == null)
            fTurret = AccessTools.Field(typeof(MiniTurretFireController), "entityTurret");
        var turret = fTurret != null ? fTurret.GetValue(__instance) as EntityTurret : null;
        if (turret == null || !WantsRotation(turret)) return;

        var alive = _target as EntityAlive;
        if (alive == null || alive.Buffs == null) return;

        if (!alive.Buffs.HasBuff(BuffShocked) && !alive.Buffs.HasBuff(BuffImmune)) return;

        __result = true;

        if (DBG) LogSkip(turret, alive);
    }

    // ------------------------------------------------------------------------------------
    // Diagnose. `shouldIgnoreTarget` laeuft mehrmals pro Frame durch jeden Kandidaten - eine
    // Zeile pro Aufruf waere ein Logbuch von hundert Zeilen je Sekunde. Deshalb nur beim
    // WECHSEL: eine Zeile, wenn ein Ziel neu als erledigt gilt.
    private static readonly Dictionary<int, int> lastSkipped = new Dictionary<int, int>();

    private static void LogSkip(EntityTurret turret, EntityAlive target)
    {
        int prev;
        if (lastSkipped.TryGetValue(turret.entityId, out prev) && prev == target.entityId) return;
        lastSkipped[turret.entityId] = target.entityId;

        Debug.Log(string.Format(
            "[AgentArmoryShock] turret {0}: Ziel {1} ({2}) ist erledigt ({3}), schalte weiter",
            turret.entityId, target.entityId, target.EntityName,
            target.Buffs.HasBuff(BuffShocked) ? "geschockt" : "immun"));
    }

    // Vom Abbau-Pfad, damit die Diagnosetabelle nicht mit der Zeit vollaeuft.
    public static void Forget(int turretEntityId)
    {
        lastSkipped.Remove(turretEntityId);
    }
}
