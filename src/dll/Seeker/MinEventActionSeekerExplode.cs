using System;
using System.Globalization;
using System.Xml.Linq;
using UnityEngine;

// Buff triggered_effect action="SeekerExplode".
// Resolved cross-assembly via ReflectionHelpers.GetTypeWithPrefix("MinEventAction", "SeekerExplode").
//
// WARUM NICHT das vanilla action="Explode":
// `MinEventActionExplode.Execute` uebergibt `target.entityId` als `_entityId` an
// `GameManager.ExplosionServer` (IL_00cc). Diese Id ist die ALLEINIGE Zuschreibung des
// Explosionsschadens, und daran haengen zwei Dinge (beide IL-belegt in
// `Explosion.AttackEntites`):
//   1. XP:    `EntityAlive verursacher = world.GetEntity(_entityThatCausedExplosion);`
//             `if (verursacher is EntityPlayer p) p.AddKillXP(opfer, itemValue, 1f);`
//             -> ist der Verursacher unsere Drohne (kein EntityPlayer), faellt der Aufruf aus:
//                genau deshalb gab es fuer Seeker-Kills KEINE Erfahrungspunkte.
//   2. Kill-Zuschreibung: `new DamageSourceEntity(..., _entityThatCausedExplosion, ...)` setzt
//      `DamageSource.ownerEntityId`. `EntityAlive.ProcessDamageResponseLocal` macht daraus
//      `entityThatKilledMe = world.GetEntity(source.getEntityId())`, und `OnEntityDeath` ruft
//      `AwardKill(entityThatKilledMe)`, das seinerseits `if (killer is EntityPlayer)` prueft.
//      Deshalb stand im Log "zombieYo killed by entitySeekerChild" statt by <Spieler>.
//
// Diese Action ist eine 1:1-Kopie von MinEventActionExplode (gleiche XML-Attribute, gleiche
// ExplosionData, gleiche ParticleIndex 13) mit genau EINER Aenderung: als Verursacher wird
// `belongsPlayerId` uebergeben, also der Spieler, der die Granate geworfen hat. Faellt die
// Owner-Kette aus (belongsPlayerId == -1), verhaelt sie sich exakt wie vanilla.
//
// Nebeneffekt, der frueher zusaetzlich im Weg stand: der Seeker wird 0.1s nach der Detonation
// per MarkToUnload entfernt: dieselbe Verzoegerung, mit der die Explosion aufgeloest wird. Bei
// Zuschreibung auf die Drohne konnte `world.GetEntity(id)` also bereits null liefern. Der
// Spieler existiert dagegen garantiert weiter.
public class MinEventActionSeekerExplode : MinEventActionTargetedBase
{
    // Defaults wie vanilla MinEventActionExplode (aus dessen .ctor uebernommen), damit ein
    // fehlendes XML-Attribut sich identisch verhaelt.
    private int blastPower = 75;
    private int blockDamage = 1;
    private int blockRadius = 4;
    private int entityDamage = 5000;
    private int entityRadius = 3;
    private string blockTags = "";
    private EnumDamageTypes damageType = EnumDamageTypes.Corrosive;   // vanilla-Default (6)

    private const bool DBG = false;  // vor Release auf false, wie in EAISeekerDetonate/EAISeekerPod

    // Die ItemValue, die als Schadensquelle mitgegeben wird. DARF NICHT null SEIN, sobald der
    // Verursacher ein SPIELER ist:
    //   `Explosion.AttackEntites` schreibt am Ende UNBEDINGT
    //       verursacher.MinEventContext.ItemValue = _itemValueExplosionSource;   (IL_0560)
    //   Mit null loescht das dauerhaft die ItemValue im MinEventContext des Spielers. Jeder
    //   spaetere `EffectManager.GetValue` kopiert diesen Kontext nach `CachedEventParam`, und
    //   `ItemHasTags.IsValid` macht dort `_params.ItemValue.IsEmpty()` OHNE Null-Pruefung
    //   -> NullReferenceException, ausgeloest ueber PlayerStealth.NotifyNoise (jede weitere
    //   Geraeuschbewertung des Spielers). Genau das war die NRE-Flut nach dem ersten XP-Build.
    //   Vanilla trifft die Kombination nie: wo ein Spieler Verursacher ist, kommt die Explosion
    //   immer aus einem Item (Granate/Rakete), und vanilla `Explode` (das null uebergibt) hat
    //   als Verursacher eine Entity, keinen Spieler.
    // `ItemValue.None` ist KEIN Ersatz: dessen ItemClass ist null, und AttackEntites
    // dereferenziert `_itemValueExplosionSource.ItemClass.ItemTags` ungeprueft (IL_0026).
    // Die geworfene Granate ist ausserdem die inhaltlich richtige Quelle: AddKillXP bekommt sie
    // als `itemUsed`, damit zaehlen Kills auf dieses Item (Challenges/Perk-Boni).
    private static ItemValue sourceItem;

    public override bool ParseXmlAttribute(XAttribute _attribute)
    {
        if (base.ParseXmlAttribute(_attribute)) return true;

        switch (_attribute.Name.LocalName)
        {
            case "blast_power":   blastPower   = ParseInt(_attribute.Value); return true;
            case "block_damage":  blockDamage  = ParseInt(_attribute.Value); return true;
            case "block_radius":  blockRadius  = ParseInt(_attribute.Value); return true;
            case "block_tags":    blockTags    = _attribute.Value;           return true;
            case "entity_damage": entityDamage = ParseInt(_attribute.Value); return true;
            case "entity_radius": entityRadius = ParseInt(_attribute.Value); return true;
            case "damage_type":
                damageType = (EnumDamageTypes)Enum.Parse(typeof(EnumDamageTypes), _attribute.Value);
                return true;
        }
        return false;
    }

    private static int ParseInt(string s)
    {
        int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out int v);
        return v;
    }

    public override void Execute(MinEventParams _params)
    {
        // Wie vanilla: Explosionen gehoeren dem Server. Ohne diesen Guard wuerde ein Client
        // die Explosion zusaetzlich lokal ausloesen.
        if (ConnectionManager.Instance == null || !ConnectionManager.Instance.IsServer) return;

        if (sourceItem == null)
            sourceItem = ItemClass.GetItem(Patch_ExplosionServer_SpawnPod.ThrownItemName, false);

        for (int i = 0; i < targets.Count; i++)
        {
            EntityAlive e = targets[i];
            if (e == null) continue;

            ExplosionData data = new ExplosionData();
            data.BlastPower    = blastPower;
            data.BlockDamage   = blockDamage;
            data.BlockRadius   = blockRadius;
            data.BlockTags     = blockTags;
            data.EntityDamage  = entityDamage;
            data.EntityRadius  = entityRadius;
            data.DamageType    = damageType;
            data.ParticleIndex = 13;   // derselbe Partikeleffekt, den vanilla Explode setzt

            // DER EIGENTLICHE UNTERSCHIED: Kill und XP dem Werfer gutschreiben.
            // belongsPlayerId wird beim Wurf gesetzt (Patch_ExplosionServer) und beim Burst
            // an die Kinder vererbt (MinEventActionSpawnSeekers). -1 = unbekannt -> vanilla.
            int attributeTo = e.belongsPlayerId >= 0 ? e.belongsPlayerId : e.entityId;

            // Verifikation im Log: steht hier owner=-1, ist die Besitzerkette unterbrochen
            // (Wurf -> Pod -> Kind) und es gibt weiterhin keine XP.
            if (DBG)
                Debug.Log($"[SeekerXpDbg] seeker {e.entityId} explodes, owner={e.belongsPlayerId} " +
                          $"-> attributed to entity {attributeTo}");

            // Der Postfix, der aus einem Granatenblast einen Pod macht, erkennt genau diese
            // ItemValue: waehrend UNSERES Aufrufs muss er stillhalten, sonst spawnt jede
            // Detonation einen neuen Pod (Kettenreaktion). Der Postfix laeuft synchron am Ende
            // von ExplosionServer, also noch innerhalb dieses try/finally.
            Patch_ExplosionServer_SpawnPod.SuppressPodSpawn = true;
            try
            {
                GameManager.Instance.ExplosionServer(
                    e.getHeadPosition(), e.GetBlockPosition(), e.qrotation,
                    data, attributeTo, 0.1f, false, sourceItem);
            }
            finally
            {
                Patch_ExplosionServer_SpawnPod.SuppressPodSpawn = false;
            }
        }
    }
}
