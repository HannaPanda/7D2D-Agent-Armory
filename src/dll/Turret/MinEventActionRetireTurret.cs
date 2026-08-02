using UnityEngine;

// Buff triggered_effect action="RetireTurret, AgentArmory".
// Aufloesung cross-assembly ueber ReflectionHelpers.GetTypeWithPrefix("MinEventAction",
// "RetireTurret") - deshalb MUSS die XML den Assemblynamen mitgeben und die Klasse im globalen
// Namensraum liegen.
//
// Haengt am `onSelfBuffFinish` von `buffAgentTurretLifetime`: die Lebenszeit ist abgelaufen,
// das Turret wird abgebaut.
//
// ⚠ WARUM DIE LEBENSZEIT AN EINEM BUFF HAENGT UND NICHT AN EINER Dictionary<entityId, float>:
// Buffdauern werden mit der Entity gespeichert, ein statisches Dictionary ist nach dem naechsten
// Laden leer. Ein Turret, das einen Speicherstand ueberlebt, waere damit unsterblich - und
// genau das faellt beim Testen nie auf, weil man selten mitten in den 90 Sekunden speichert.
// Der zweite Gewinn: "Buff abgelaufen" ist ein EREIGNIS. Wuerden wir stattdessen jeden Frame
// pruefen, ob der Buff noch da ist, koennten wir "abgelaufen" nicht von "nie gesetzt"
// unterscheiden - und ein Tippfehler im Buffnamen wuerde jedes Turret sofort nach dem Aufbau
// verschwinden lassen.
public class MinEventActionRetireTurret : MinEventActionTargetedBase
{
    public override void Execute(MinEventParams _params)
    {
        var turret = _params.Self as EntityTurret;
        if (turret == null) return;
        TurretRetire.Retire(turret, "Lebenszeit abgelaufen");
    }
}

// Gemeinsamer Abbau-Pfad fuer beide Enden (Lebenszeit und leeres Magazin).
public static class TurretRetire
{
    // Diagnoseausgabe waehrend der Einfuehrung. VOR DEM RELEASE AUF false - der Mod schreibt
    // auf einem sauberen Lauf sonst mehr als die zwei Zeilen aus ModEntry, und genau die
    // nutzt die Testbench als Beweis.
    private const bool DBG = true;

    public static void Retire(EntityTurret turret, string reason)
    {
        if (turret == null || turret.IsDead()) return;

        // Aus der Besitzerliste nehmen. Nicht toedlich, wenn es unterbleibt - die Zaehlschleife
        // in `EntityTurret.OnUpdateEntity` holt jede Id ueber `world.GetEntity` und ueberspringt
        // null (IL_033d) -, aber ein wachsender Friedhof in `ownedEntities` ist unnoetig.
        var world = turret.world;
        if (world != null && turret.belongsPlayerId != -1)
        {
            var owner = world.GetEntity(turret.belongsPlayerId) as EntityAlive;
            if (owner != null) owner.RemoveOwnedEntity(turret);
        }

        // MarkToUnload statt SetDead: kein Kadaver, keine Loot-Kiste, kein Aufsammeln der
        // `LootList` roboticTurret, die junkTurretGun vererbt. Das Turret war ein
        // Einwegartikel - es soll verschwinden, nicht sterben.
        turret.MarkToUnload();

        if (DBG)
            Debug.Log("[AgentArmory] Turret " + turret.entityId + " abgebaut: " + reason);
    }
}
