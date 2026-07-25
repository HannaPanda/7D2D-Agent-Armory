using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using UnityEngine;

// Buff triggered_effect action="SpawnSeekers".
// Resolved cross-assembly via ReflectionHelpers.GetTypeWithPrefix("MinEventAction", "SpawnSeekers").
// Fired from the pod's arming buff onSelfBuffFinish: bursts into N child seekers, then the pod dies.
public class MinEventActionSpawnSeekers : MinEventActionTargetedBase
{
    private string entityName = "entitySeekerChild";
    private string childArmBuff = "buffSeekerChildArm";
    private int count = 5;
    private bool killSelf = true;

    public override bool ParseXmlAttribute(XAttribute _attribute)
    {
        if (base.ParseXmlAttribute(_attribute)) return true;

        switch (_attribute.Name.LocalName)
        {
            case "entity":    entityName   = _attribute.Value; return true;
            case "arm_buff":  childArmBuff = _attribute.Value; return true;
            case "count":     count  = int.Parse(_attribute.Value, CultureInfo.InvariantCulture); return true;
            // `spread` wird seit 2026-07-25 IGNORIERT: die Kinder entstehen direkt aus dem Pod
            // (siehe Execute). Das Attribut bleibt gueltig, damit bestehende XML nicht bricht -
            // ein unbekanntes Attribut wuerde beim Laden als Fehler auflaufen.
            case "spread":    return true;
            case "kill_self": killSelf = _attribute.Value == "true"; return true;
        }
        return false;
    }

    public override void Execute(MinEventParams _params)
    {
        EntityAlive self = _params.Self;
        if (self == null || self.world == null) return;

        World world = self.world;
        GameRandom rand = world.rand;
        Vector3 basePos = self.position;

        // Collect nearby zombies so we can hand each child a DIFFERENT starting target
        // (round-robin) — this is what makes the cluster fan out instead of dogpiling
        // the single nearest zombie. EAISeekerDetonate keeps re-spreading after that.
        // Scan radius ~15m (box size 30) to match the child task's acquire_range. Keeps the
        // initial round-robin targets LOCAL — a wide scan handed children distant zombies at
        // spawn, so they'd charge off across the map (the "targeting feels very far" issue).
        List<EntityAlive> targets = new List<EntityAlive>();
        List<Entity> scan = new List<Entity>();
        world.GetEntitiesInBounds(typeof(EntityEnemy),
            new Bounds(basePos, new Vector3(30f, 30f, 30f)), scan);
        const float initialTargetRange = 15f;
        float initSq = initialTargetRange * initialTargetRange;
        for (int i = 0; i < scan.Count; i++)
            if (scan[i] is EntityAlive z && !z.IsDead() && !z.sleepingOrWakingUp
                && (z.position - basePos).sqrMagnitude <= initSq)
                targets.Add(z);   // exclude dormant POI sleepers

        int assigned = 0;
        // Zufaellige Ringdrehung, damit nicht jeder Wurf dasselbe Muster auslegt.
        float ringStartAngle = rand.RandomFloat * 2f * Mathf.PI;
        for (int i = 0; i < count; i++)
        {
            // DIE KINDER ENTSTEHEN AUS DEM POD - exakt auf seiner Position (2026-07-25).
            //
            // Vorgeschichte: hier stand erst ein blinder Zufalls-XZ-Versatz (setzte Kinder in Waende
            // und ueber Kanten), danach `FindRandomSpawnPointNearPosition`. Beides sucht eine NEUE
            // Position und muss deren Gueltigkeit erraten. Der Spawnpunkt-Sucher liefert dazu
            // GANZZAHLIGE Blockkoordinaten und prueft gegen ein menschengrosses Volumen mit 4
            // Bloecken vertikalem Spielraum - fuer eine 0.4 m hohe Drohne kann das eine volle
            // Blockhoehe neben der Oberflaeche liegen.
            //
            // Messung Log `..._12-52-59` (erster Lauf mit korrekter Kapsel): der POD landete sauber
            // (`onGround=True canNav=True` bei exakt y=38.00), die KINDER waren nur in 12% der
            // Samples am Boden, 60 Rettungen mit Sturz-Median 3.36 m, und 65 von 80 schnellen
            // Samples waren `onGround=False` - die Geschwindigkeit entstand also im freien Fall.
            //
            // Die Podposition ist die EINZIGE Koordinate, die wir nicht schaetzen muessen: der Pod
            // ist nicht gesetzt, sondern GEFALLEN und vom Motor als Ruhelage bestaetigt worden, und
            // das `hasLanded`-Gate in EAISeekerPod garantiert, dass das im Bursttick schon passiert
            // ist. Deshalb entstehen die Kinder jetzt direkt aus dem Pod heraus - so wie der Pod in
            // Division auch aufplatzt, statt seine Seeker in der Gegend zu verteilen.
            //
            // Der einzige Zusatz ist ein kleiner Ring, damit die Kinder nicht deckungsgleich starten:
            // vollstaendig ueberlappende Kapseln werden vom Motor mit voller Wucht auseinander
            // gedrueckt, und genau dieses Wegschleudern haben wir die letzten Runden bekaempft.
            // Der Ring waechst mit der Anzahl, sodass BENACHBARTE Kinder sich gerade nicht beruehren
            // (Bogenlaenge >= 2.2 * Radius). Er bleibt dabei winzig - bei 5 Kindern 0.35 m, also
            // noch innerhalb des Podradius von 0.309. Wo der Pod steht, ist damit auch der Ring gueltig.
            const float childRadius = 0.17f;   // spiegelt die Prefab-Kapsel; zu gross ist harmlos, zu klein nicht
            float ringR = Mathf.Max(0.35f, count * 2.2f * childRadius / (2f * Mathf.PI));
            float ang = (count > 0 ? (i * 2f * Mathf.PI / count) : 0f) + ringStartAngle;
            Vector3 pos = new Vector3(basePos.x + Mathf.Cos(ang) * ringR,
                                      basePos.y,
                                      basePos.z + Mathf.Sin(ang) * ringR);

            Vector3 rot = new Vector3(0f, rand.RandomFloat * 360f, 0f);
            // Besitzer vom Pod an die Kinder weiterreichen: nur so kann die Detonation des
            // Kindes Kill + XP dem Werfer gutschreiben (siehe MinEventActionSeekerExplode).
            EntityAlive child = SeekerSpawner.Spawn(world, entityName, pos, rot, childArmBuff,
                                                    self.belongsPlayerId);
            if (child != null && targets.Count > 0)
            {
                child.SetAttackTarget(targets[assigned % targets.Count], 200);
                assigned++;
            }
        }

        // Corpse-free removal (MarkToUnload), so the pod simply vanishes after bursting
        // instead of leaving a dead-rabbit body behind (SetDead would leave a corpse).
        if (killSelf) self.MarkToUnload();
    }
}
