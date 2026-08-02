using HarmonyLib;
using UnityEngine;

// Wurf -> Aufschlag -> Turret. Gleicher Mechanismus wie beim Cluster Seeker: die geworfene Box
// ist eine ganz normale Zeitladung mit harmloser Explosion, und wir benutzen die Detonation
// nur als Zustellereignis mit fertiger Weltposition.
//
// ⚠ BEWUSST EIN ZWEITER POSTFIX statt einer Zusammenlegung mit
// `Patch_ExplosionServer_SpawnPod`. Beide filtern auf ihr eigenes Item und sehen sich deshalb
// nicht: eine Seeker-Detonation traegt `thrownSeekerCluster` und faellt hier durch
// `TurretSpec.For` heraus, eine Turret-Box traegt kein `thrownSeekerCluster` und faellt dort
// heraus. Die Kaskadensperre des Seekers (SuppressPodSpawn) brauchen wir nicht - eine
// Turret-Box explodiert genau einmal, das aufgebaute Turret loest nie eine Explosion aus.
// Ein Umbau des funktionierenden Seeker-Pfads waere reines Risiko ohne Gegenwert.
[HarmonyPatch(typeof(GameManager), "ExplosionServer")]
public static class Patch_ExplosionServer_DeployTurret
{
    // _entityId ist bei einer geworfenen Ladung der WERFER:
    // `ItemClassTimeBomb.OnDroppedUpdate` reicht `ItemWorldData.belongsEntityId` durch
    // (IL_01e2). Genau diese Id braucht das Turret als Besitzer.
    static void Postfix(GameManager __instance, Vector3 _worldPos, int _entityId,
                        ItemValue _itemValueExplosionSource)
    {
        if (__instance == null || _itemValueExplosionSource == null) return;

        TurretSpec spec = TurretSpec.For(_itemValueExplosionSource.ItemClass);
        if (spec == null) return;   // irgendeine andere Granate - nicht unsere Baustelle

        World world = __instance.World;
        if (world == null) return;

        // Ein Stueck ueber dem Einschlagpunkt aufbauen und fallen lassen: EntityTurret bringt
        // dafuer eigene Logik mit (isFalling/fallPos + RotateToGround an der entity_class).
        // Direkt auf der Aufschlagshoehe zu spawnen setzt das Turret bei einer schraegen
        // Landung mit der Unterkante in den Boden.
        TurretDeployer.Deploy(world, spec, _worldPos + Vector3.up * 0.15f, _entityId);
    }
}
