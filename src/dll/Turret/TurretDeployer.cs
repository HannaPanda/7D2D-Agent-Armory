using System.Collections.Generic;
using UnityEngine;

// Baut ein EntityTurret an einer Weltposition auf, ohne dass ein Spieler es in der Hand haelt.
//
// Die Reihenfolge unten ist KEINE eigene Erfindung, sondern 1:1 die von
// `ItemActionSpawnTurret.ExecuteAction` (IL_01b7..IL_0376) - dieselben Felder, dieselbe
// Reihenfolge, nur ohne UI, Platzierungsvorschau und Inventarabzug. Wer hier etwas weglaesst,
// bekommt ein Turret, das dasteht und nichts tut, ohne eine einzige Fehlerzeile im Log.
public static class TurretDeployer
{
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

        var rot = new Vector3(0f, ChooseFacing(world, owner, pos), 0f);

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

        // VOR dem Spawn: die Uhr liegt in der Metadata der OriginalItemValue, und die soll
        // schon beim ersten `OnUpdateEntity` stehen. Fehlt sie dort, baut `TurretLifetime.Tick`
        // das Turret sofort wieder ab - absichtlich, siehe die Begruendung dort.
        TurretLifetime.Arm(turret, spec.Lifetime);

        owner.AddOwnedEntity(turret);
        world.SpawnEntityInWorld(turret);

        return turret;
    }

    // In welche Richtung das aufgebaute Turret schaut.
    //
    // ⚠ WARUM DAS UEBERHAUPT ZAEHLT, obwohl unsere Turrets `YawRange="360"` haben: der
    // Schwenkbereich wird zwar nicht mehr beschnitten, aber der Kopf muss trotzdem DREHEN,
    // und `TurnSpeed` liegt je nach Sorte bei 14 bis 40 Grad pro Sekunde. Ein Sniper-Turret,
    // das im ungünstigsten Fall 180 Grad zurücklegen muss, verliert dreizehn Sekunden - bei
    // 120 s Laufzeit ein Neuntel seines Lebens, bevor der erste Schuss fällt.
    //
    // Vorher stand hier `owner.rotation.y`, also die Blickrichtung des Werfers IM MOMENT DES
    // AUFSCHLAGS. Das ist nicht die Wurfrichtung: zwischen Wurf und Landung vergeht bei einem
    // weiten Wurf über eine Sekunde, und in der dreht man sich. Praktisch war die Ausrichtung
    // damit beliebig - genau der im Test beobachtete Effekt.
    private static float ChooseFacing(World world, EntityAlive owner, Vector3 pos)
    {
        // 1. Wahl: der naechste Gegner. Wer ein Turret mitten in einen Kampf wirft, will, dass
        //    es sofort schiesst, nicht dass es sich erst umsieht.
        var scan = new List<Entity>();
        world.GetEntitiesInBounds(typeof(EntityEnemy),
            new Bounds(pos, new Vector3(ScanRange * 2f, ScanRange * 2f, ScanRange * 2f)), scan);
        Entity nearest = null;
        float bestSq = ScanRange * ScanRange;
        for (int i = 0; i < scan.Count; i++)
        {
            var e = scan[i] as EntityAlive;
            // Schlafende POI-Zombies zaehlen nicht: sie sind kein Grund, das Turret in eine
            // Ecke schauen zu lassen, in der gerade nichts passiert. Dieselbe Filterung
            // benutzt der Seeker bei seiner Zielsuche.
            if (e == null || e.IsDead() || e.sleepingOrWakingUp) continue;
            float d = (e.position - pos).sqrMagnitude;
            if (d < bestSq) { bestSq = d; nearest = e; }
        }
        if (nearest != null) return YawTowards(pos, nearest.position);

        // 2. Wahl: die WURFRICHTUNG, also vom Werfer zum Landepunkt. Anders als die
        //    Blickrichtung ist die geometrisch bestimmt und aendert sich nicht mehr, waehrend
        //    die Box fliegt - man zeigt mit dem Wurf, wohin das Turret schauen soll.
        Vector3 throwDir = pos - owner.position;
        throwDir.y = 0f;
        if (throwDir.sqrMagnitude > 0.25f) return Quaternion.LookRotation(throwDir).eulerAngles.y;

        // 3. Wahl: vor die Fuesse geworfen, keine Richtung ableitbar - dann eben die
        //    Blickrichtung des Werfers.
        return owner.rotation.y;
    }

    // Radius, in dem beim Aufbau nach einem Gegner gesucht wird. Bewusst grosszuegiger als die
    // kurzen Turretreichweiten (Shotgun 9 m, Flamme 6 m): es geht nicht darum, ob das Turret
    // schon schiessen kann, sondern in welche Richtung es sinnvollerweise wartet.
    private const float ScanRange = 25f;

    private static float YawTowards(Vector3 from, Vector3 to)
    {
        Vector3 dir = to - from;
        dir.y = 0f;   // nur der horizontale Anteil; die Hoehe macht der PitchController
        if (dir.sqrMagnitude < 0.0001f) return 0f;
        return Quaternion.LookRotation(dir).eulerAngles.y;
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
