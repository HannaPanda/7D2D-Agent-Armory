using UnityEngine;

// Baut ein EntityTurret an einer Weltposition auf, ohne dass ein Spieler es in der Hand haelt.
//
// Die Reihenfolge unten ist KEINE eigene Erfindung, sondern 1:1 die von
// `ItemActionSpawnTurret.ExecuteAction` (IL_01b7..IL_0376) - dieselben Felder, dieselbe
// Reihenfolge, nur ohne UI, Platzierungsvorschau und Inventarabzug. Wer hier etwas weglaesst,
// bekommt ein Turret, das dasteht und nichts tut, ohne eine einzige Fehlerzeile im Log.
public static class TurretDeployer
{
    // Lebenszeit-Traeger. Bewusst ein BUFF und keine statische Dictionary<entityId, float>:
    // Buffs werden mit der Entity gespeichert, ein Dictionary ist nach dem naechsten Laden weg
    // und das Turret waere unsterblich. Der Buff tut selbst nichts - `Patch_TurretRuntime`
    // liest nur, ob er noch da ist.
    public const string LifetimeBuff = "buffAgentTurretLifetime";

    // Gibt das aufgebaute Turret zurueck, oder null wenn etwas fehlt (mit Logzeile).
    public static EntityTurret Deploy(World world, TurretSpec spec, Vector3 pos, int ownerPlayerId)
    {
        if (world == null || spec == null) return null;

        // `EntityClass.FromString` ist nur string.GetHashCode() - es validiert nichts und darf
        // legitim negativ sein. "< 0" waere also KEINE Existenzpruefung; nur GetEntityClass
        // beantwortet die Frage. (Derselbe Fehler hat beim Seeker eine Runde gekostet.)
        int cls = EntityClass.FromString(spec.EntityClass);
        if (EntityClass.GetEntityClass(cls) == null)
        {
            Debug.LogWarning("[AgentArmory] unbekannte entity_class: " + spec.EntityClass);
            return null;
        }

        ItemValue core = ItemClass.GetItem(spec.CoreItem, false);
        if (core == null || core.ItemClass == null)
        {
            Debug.LogWarning("[AgentArmory] unbekanntes Kern-Item: " + spec.CoreItem);
            return null;
        }

        var owner = world.GetEntity(ownerPlayerId) as EntityAlive;
        if (owner == null)
        {
            // Ohne Besitzer bleibt das Turret dauerhaft aus: `EntityTurret.OnUpdateEntity`
            // rechnet `IsOn &= Owner != null` (IL_01ef), sobald belongsPlayerId gesetzt ist.
            // Lieber gar nicht aufbauen als ein totes Geraet hinstellen.
            Debug.LogWarning("[AgentArmory] Turret ohne Besitzer (id " + ownerPlayerId
                + ") - nicht aufgebaut.");
            return null;
        }

        // Blickrichtung des Werfers uebernehmen: das Turret schaut in die Richtung, in die
        // geworfen wurde. Vanilla nimmt an dieser Stelle ebenfalls nur den Yaw des Besitzers.
        var rot = new Vector3(0f, owner.rotation.y, 0f);

        Entity e = EntityFactory.CreateEntity(cls, pos, rot);
        var turret = e as EntityTurret;
        if (turret == null)
        {
            Debug.LogWarning("[AgentArmory] '" + spec.EntityClass
                + "' ist kein EntityTurret (Class-Property falsch?).");
            if (e != null) e.MarkToUnload();
            return null;
        }

        // StaticSpawner (2), NICHT Dynamic - genau wie ItemActionSpawnTurret (IL_01f0).
        // Dynamic wuerde das Turret dem Wellen-Aufraeumer des dynamischen Spawners ausliefern,
        // der es beim Weglaufen des Spielers entfernen darf.
        turret.SetSpawnerSource(EnumSpawnerSource.StaticSpawner);

        turret.factionId = owner.factionId;
        // factionRank des Besitzers minus 1: so steht das Turret in der Fraktionshierarchie
        // unter seinem Besitzer (vanilla IL_0241..IL_0249).
        turret.factionRank = (byte)(owner.factionRank - 1);
        // belongsPlayerId ist das Feld, an dem Kill- und XP-Zuschreibung haengt - dieselbe
        // Erkenntnis wie beim Seeker: `MiniTurretFireController.Fire` holt darueber den
        // Spieler und uebergibt ihn als schadenverursachende Entity.
        turret.belongsPlayerId = ownerPlayerId;
        turret.OriginalItemValue = core.Clone();
        turret.OriginalItemValue.SelectedAmmoTypeIndex = (byte)spec.AmmoIndex;
        turret.groundPosition = pos;
        turret.OwnerID = ResolveOwnerId(ownerPlayerId);
        // ForceOn ist eine EIN-FRAME-Gnade: `OnUpdateEntity` setzt es am Ende selbst wieder auf
        // false. Es sorgt nur dafuer, dass das frisch geworfene Turret gegen ein aelteres
        // gewinnt, wenn JunkTurretActiveCount ausgereizt ist. Das dauerhafte Offenhalten
        // macht Patch_TurretRuntime.
        turret.ForceOn = true;

        // Munition. 0 heisst "nimm das Magazin des Kern-Items" - GetInitialMetadata rechnet
        // dafuer den MagazineSize-Effekt aus, inklusive der Perk-Boni des Besitzers.
        turret.AmmoCount = spec.AmmoCount > 0
            ? spec.AmmoCount
            : core.ItemClass.GetInitialMetadata(turret.OriginalItemValue);

        owner.AddOwnedEntity(turret);
        world.SpawnEntityInWorld(turret);

        // NACH dem Spawn: vorher gibt es die Bufflisten der Entity noch nicht zuverlaessig.
        // Die Dauer kommt aus der Box, nicht aus der buffs.xml - deshalb der explizite
        // Wert statt -1.
        if (spec.Lifetime > 0f)
            turret.Buffs.AddBuff(LifetimeBuff, ownerPlayerId, false, false, spec.Lifetime);

        return turret;
    }

    // Der MP-korrekte Weg zur Besitzerkennung. Vanilla nimmt hier
    // `PlatformManager.InternalLocalUserIdentifier`, weil ItemActionSpawnTurret auf dem
    // CLIENT des Werfers laeuft. Unser Aufbau haengt an ExplosionServer, laeuft also auf dem
    // Server - dort waere das die Kennung des Hosts und auf einem Dedicated schlicht falsch.
    private static PlatformUserIdentifierAbs ResolveOwnerId(int playerEntityId)
    {
        var list = GameManager.Instance != null ? GameManager.Instance.GetPersistentPlayerList() : null;
        if (list == null) return null;
        var data = list.GetPlayerDataFromEntityID(playerEntityId);
        return data != null ? data.PrimaryId : null;
    }
}
