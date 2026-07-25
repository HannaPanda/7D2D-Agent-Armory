using HarmonyLib;
using UnityEngine;

// When the thrown "thrownSeekerCluster" grenade detonates, spawn one cluster pod
// at the blast position instead of dealing meaningful damage. We piggy-back on the
// vanilla explosion (harmless, tiny radius in items.xml) purely as a delivery event.
[HarmonyPatch(typeof(GameManager), "ExplosionServer")]
public static class Patch_ExplosionServer_SpawnPod
{
    public const string ThrownItemName = "thrownSeekerCluster";
    private const string PodEntityName  = "entitySeekerCluster";

    private static ItemClass cachedItem;

    // KASKADENSCHUTZ. Die Detonation eines Seekers uebergibt seit dem XP-Fix dieselbe ItemValue
    // (thrownSeekerCluster) an ExplosionServer wie der Wurf selbst: sie MUSS non-null sein und
    // eine gueltige ItemClass haben, siehe MinEventActionSeekerExplode. Ohne diese Sperre wuerde
    // der Postfix hier jede Seeker-Detonation fuer einen neuen Wurf halten und einen weiteren Pod
    // spawnen -> endlose Kettenreaktion.
    // Ein statisches Flag reicht, weil der Postfix synchron am Ende von ExplosionServer laeuft,
    // also noch innerhalb unseres try/finally (die Verzoegerung steckt in der Coroutine dahinter,
    // nicht im Aufruf).
    public static bool SuppressPodSpawn;

    // _entityId ist bei einer geworfenen Granate der WERFER: `ItemClassTimeBomb.OnDroppedUpdate`
    // uebergibt `ItemWorldData.belongsEntityId` an ExplosionServer (IL_01e2). Genau diese Id
    // brauchen wir, um Kills und XP spaeter dem Spieler gutzuschreiben.
    static void Postfix(GameManager __instance, Vector3 _worldPos, int _entityId,
                        ItemValue _itemValueExplosionSource)
    {
        if (SuppressPodSpawn) return;   // eigene Seeker-Detonation, kein neuer Pod
        if (_itemValueExplosionSource == null || __instance == null) return;

        if (cachedItem == null)
            cachedItem = ItemClass.GetItemClass(ThrownItemName, false);
        if (cachedItem == null) return;

        if (_itemValueExplosionSource.ItemClass != cachedItem) return;

        World world = __instance.World;
        if (world == null) return;

        // No arm buff here: the pod follows the player and arms itself (via EAISeekerPod)
        // only once a zombie comes within range. It is not a timed bomb anymore.
        float yaw = world.rand.RandomFloat * 360f;
        SeekerSpawner.Spawn(world, PodEntityName, _worldPos + Vector3.up * 0.3f,
            new Vector3(0f, yaw, 0f), null, _entityId);
    }
}
