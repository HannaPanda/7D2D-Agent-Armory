using UnityEngine;

// Shared server-side spawn helper used by both the throw->pod Harmony hook
// and the pod->children MinEventAction.
public static class SeekerSpawner
{
    // Returns the spawned entity (or null) so callers can e.g. assign it a target.
    //
    // ownerPlayerId = the player this seeker acts on behalf of (the thrower). Stored in the
    // vanilla field `Entity.belongsPlayerId` (same field the Junk Turret and the drone use,
    // see EntityTurret.PostInit / ItemActionSpawnTurret.ExecuteAction). It is what lets the
    // detonation credit kill + XP to the player: MinEventActionSeekerExplode reads it back and
    // passes it to ExplosionServer as the causing entity. -1 = unknown (vanilla behaviour).
    // Safe to set on a non-drone entity: every reader of belongsPlayerId is inside a
    // drone/turret/vehicle/item specific class, nothing on the generic Entity/EntityAlive path.
    public static EntityAlive Spawn(World world, string entityName, Vector3 pos, Vector3 rot,
                                    string armBuff = null, int ownerPlayerId = -1)
    {
        if (world == null) return null;

        // EntityClass.FromString is just string.GetHashCode() — it validates nothing
        // and legitimately returns negative hashes, so "cls < 0" is NOT an existence
        // check. EntityClass.list is keyed by that hash; GetEntityClass does a
        // TryGetValue and returns null iff the class isn't registered.
        int cls = EntityClass.FromString(entityName);
        if (EntityClass.GetEntityClass(cls) == null)
        {
            Debug.LogWarning("[SeekerCluster] unknown entity class: " + entityName);
            return null;
        }

        Entity e = EntityFactory.CreateEntity(cls, pos, rot);
        if (e == null) return null;

        e.SetSpawnerSource(EnumSpawnerSource.Dynamic);
        // Vor SpawnEntityInWorld setzen, damit der Wert schon beim ersten Tick steht.
        if (ownerPlayerId >= 0) e.belongsPlayerId = ownerPlayerId;
        world.SpawnEntityInWorld(e);

        EntityAlive alive = e as EntityAlive;
        if (alive != null && !string.IsNullOrEmpty(armBuff))
            alive.Buffs.AddBuff(armBuff, -1, false, false, -1f);

        return alive;
    }
}
