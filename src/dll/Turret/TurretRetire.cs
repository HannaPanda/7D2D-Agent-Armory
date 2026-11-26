using UnityEngine;

// Gemeinsamer Abbau-Pfad fuer beide Enden: leeres Magazin und abgelaufene Lebenszeit.
public static class TurretRetire
{
    // Diagnoseausgabe waehrend der Einfuehrung. VOR DEM RELEASE AUF false - der Mod schreibt
    // auf einem sauberen Lauf sonst mehr als die zwei Zeilen aus ModEntry, und genau die
    // nutzt die Testbench als Beweis.
    private const bool DBG = false;

    public static void Retire(EntityTurret turret, string reason)
    {
        if (turret == null || turret.IsDead()) return;

        TurretLifetime.Forget(turret.entityId);
        // Das Partikelexemplar stirbt mit dem Modell des Turrets, der Schleifenton nicht -
        // der haengt an der Entity-Id im Audio-Manager und braucht ein ausdrueckliches Stop.
        TurretFlameJet.Forget(turret);
        Patch_TurretShockRotation.Forget(turret.entityId);

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
