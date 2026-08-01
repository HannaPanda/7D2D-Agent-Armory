using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// Custom AI task for the MAIN pod (entitySeekerCluster).
//   SeekerPod,SeekerCluster class=EntityPlayer,600;detect_range=12;arm_buff=buffSeekerClusterArm
// Resolved via EAIManager.GetType -> Type.GetType("EAI"+name) (assembly-qualified, global namespace).
//
// The pod is a companion, NOT a bomb: it follows the player around and does nothing until a
// zombie comes within detect_range. Then it applies its arming buff — whose onSelfBuffFinish
// runs SpawnSeekers (burst into children) and MarkToUnloads the pod (it vanishes, no corpse).
public class EAISeekerPod : EAIApproachAndAttackTarget
{
    private float detectRange = 12f;
    private string armBuff = "buffSeekerClusterArm";
    private bool armed;
    private const float followDist = 3.5f;   // hover this far behind the player, then hold
    // Zusatzabstand, ab dem der Pod wieder ANFAEHRT. Ohne diese Hysterese schlaegt der Zustand bei
    // d ≈ followDist jeden Tick um und der Pod zappelt statt zu halten.
    private const float followHysteresis = 1.5f;
    private bool following;

    // ERST LANDEN, DANN SCHARF WERDEN (2026-07-25).
    // Vorher armte der Pod schon in seiner allerersten Update() - in beiden Logs vom 24.07.
    // steht in JEDER Pod-Zeile `armed=True` bei gleichzeitig `onGround=False collFlags=None`.
    // Der Pod spawnt an der Explosionsposition + 0.3m und sinkt danach nur mit 1.6 m/s
    // (Patch_SeekerFallClamp deckelt die Fallgeschwindigkeit gegen Tunneling). Platzt er in
    // dieser Sinkphase - buffSeekerClusterArm dauert nur 0.75s -, dann ist `self.position` in
    // MinEventActionSpawnSeekers ein Punkt in der LUFT: FindRandomSpawnPointNearPosition findet
    // dort keinen gültigen Bodenpunkt, der Fallback setzt die Kinder ebenfalls in die Luft, und
    // die gleiten dann ungegroundet los. Ungegroundet heisst `CanNavigatePath()==false`, also
    // kein A*, also nur der grobe Direktschub - genau der Modus, in dem sie durch Wände ziehen.
    // Mit dem Gate unten burstet der Pod garantiert von festem Boden aus.
    private bool hasLanded;
    private float bornTime = -1f;
    // Notausstieg, falls onGround aus irgendeinem Grund nie true wird: der Pod soll dann
    // trotzdem funktionieren, statt stumm liegen zu bleiben.
    private const float maxAirTime = 3f;

    // ===== ABLAUF: LANDEN -> KURZ SACKEN -> FEINDCHECK -> ERST DANN FOLGEN =====
    //
    // Vorher lief die Follow-Bewegung ab dem allerersten Tick, unabhaengig von allem anderen,
    // waehrend der Feindscan auf `hasLanded` wartete. Beobachtetes Ergebnis: Granate werfen ->
    // Pod erscheint -> rennt sofort mit Vollgas zum Spieler -> merkt erst unterwegs, dass am
    // LANDEORT laengst Zombies standen -> armt und platzt auf dem Rueckweg. Die Kinder
    // entstanden damit irgendwo zwischen Wurfziel und Spieler statt dort, wo der Spieler den
    // Pod hingeworfen hat.
    // Jetzt als klare Reihenfolge: solange der Pod nicht steht, bewegt er sich gar nicht; nach
    // dem Bodenkontakt kurz sacken lassen (die ersten Frames nach der Landung sind unruhig);
    // dann der Feindcheck. Nur wenn der NICHTS findet, geht er auf Follow. Findet er etwas,
    // armt er sofort an Ort und Stelle - und bleibt zum Armen auch stehen, damit die Kinder
    // vom Landeort aus starten.
    private bool readyChecked;
    private float landedTime = -1f;
    private const float readySettleTime = 0.4f;

    private readonly List<Entity> scanBuffer = new List<Entity>();

    // Throttled diagnostics: EAISeekerPod previously had ZERO logging, so the log gave no
    // evidence at all about why "the pod doesn't follow properly". Log state ~1x/sec (not
    // every tick, to avoid the same log-spam bug AITarget-1 caused on the children).
    private const bool DBG = false;  // flip to false before release
    private float nextDbgTime = -1f;
    private static void Dbg(string s) { if (DBG) UnityEngine.Debug.Log("[SeekerPodDbg] " + s); }

    // Geschwindigkeitsmessung pro Tick, Sprünge (RESCUE-Teleport / Origin-Shift) verworfen.
    // Anlass: die Pod-Distanz zum Spieler pendelte im Log zwischen 7 und 34 Metern bei einem
    // Sample pro Sekunde - also ~15 m/s bei konfigurierten 2.2, und das auf einem gueltigen
    // A*-Pfad (`hasPath=True action=pathHold`). Diese Zeile trennt "darf" von "tut".
    private Vector3? lastTickPos;
    private float accumTravel;
    private float maxStep;
    private const float teleportStep = 2f;

    private void TrackSpeed()
    {
        Vector3 p = theEntity.position;
        if (lastTickPos.HasValue)
        {
            float step = Vector3.Distance(p, lastTickPos.Value);
            if (step <= teleportStep)
            {
                accumTravel += step;
                if (step > maxStep) maxStep = step;
            }
        }
        lastTickPos = p;
    }

    public override void SetData(Dictionary<string, string> _data)
    {
        base.SetData(_data);
        if (_data.TryGetValue("detect_range", out string d))
            float.TryParse(d, NumberStyles.Any, CultureInfo.InvariantCulture, out detectRange);
        if (_data.TryGetValue("arm_buff", out string a)) armBuff = a;
    }

    // Base Start() dereferences entityTarget (set by base.CanExecute, which we no longer call)
    // -> NRE. We drive follow/arm ourselves and need none of its setup, so no-op it.
    public override void Start() { }

    // Ebenfalls nötig, aus demselben Grund - ausführliche Herleitung in EAISeekerDetonate:
    // `base.Continue()` vergleicht das Angriffsziel gegen das protected Feld `entityTarget`,
    // das nur `base.CanExecute()` befüllt. Ohne diesen Override lieferte Continue() jeden Tick
    // false, EAITaskList beendete den Task und rief `Reset()` -> `base.Reset()` ->
    // `moveHelper.Stop()` (inkl. `navigator.clearPath()`). Der Pod hat sich damit jede eigene
    // Bewegungsanweisung im selben Tick wieder gelöscht - das war der Grund für das ruckelige
    // Folgen, das seit Tagen nicht wegzubekommen war.
    public override bool Continue()
    {
        return CanExecute();
    }

    public override bool CanExecute()
    {
        // Always run: our Update drives BOTH the follow movement and the arm scan. (We no
        // longer route through base EAIApproachAndAttackTarget — its flanking movement made
        // the pod orbit the player at high speed and clip through walls.)
        return theEntity != null && !theEntity.IsDead();
    }

    // Gedrosseltes Statuslog fuer die Phasen VOR dem Follow (Landing/Settle/Armed). Die
    // ausfuehrliche Zeile am Ende von Update() erreicht diese Phasen nicht mehr, weil dort
    // vorher returned wird - ohne das hier waere der neue Ablauf im Log unsichtbar.
    private void DbgTick(string phase)
    {
        if (!DBG || Time.time < nextDbgTime) return;
        nextDbgTime = Time.time + 1f;
        Dbg($"pod {theEntity.entityId} phase={phase} landed={hasLanded} ready={readyChecked} " +
            $"armed={armed} onGround={theEntity.onGround} canNav={theEntity.CanNavigatePath()} " +
            $"pos={theEntity.position}");
    }

    public override void Update()
    {
        // NOT calling base.Update() — see CanExecute (avoids the flanking/circling movement).
        if (theEntity == null || theEntity.IsDead() || theEntity.world == null) return;

        TrackSpeed();

        if (bornTime < 0f) bornTime = Time.time;
        if (!hasLanded && (theEntity.onGround || Time.time - bornTime >= maxAirTime))
        {
            hasLanded = true;
            landedTime = Time.time;
        }

        // PHASE 1 - noch in der Luft / beim Aufsetzen: NICHT bewegen. Vorher lief die
        // Follow-Bewegung schon hier los, weshalb der Pod losrannte, bevor er den Landeort
        // ueberhaupt geprueft hatte.
        if (!hasLanded)
        {
            theEntity.moveHelper.Stop();
            DbgTick("LANDING");
            return;
        }

        // PHASE 2 - kurz sacken lassen. Die ersten Frames nach dem Bodenkontakt sind unruhig
        // (Kapselkorrektur, Restimpuls), ein Scan daraus waere ein Zufallswert.
        if (!readyChecked && Time.time - landedTime < readySettleTime)
        {
            theEntity.moveHelper.Stop();
            DbgTick("SETTLE");
            return;
        }

        // PHASE 3 - Feindcheck. Laeuft ab jetzt jeden Tick weiter, damit der Pod auch spaeter
        // noch armt, wenn im Follow-Modus ein Zombie in Reichweite kommt.
        if (!armed)
        {
            Vector3 pos = theEntity.position;
            Bounds bb = new Bounds(pos, new Vector3(detectRange * 2f, detectRange * 2f, detectRange * 2f));
            scanBuffer.Clear();
            theEntity.world.GetEntitiesInBounds(typeof(EntityEnemy), bb, scanBuffer);
            float maxSq = detectRange * detectRange;
            for (int i = 0; i < scanBuffer.Count; i++)
            {
                if (!(scanBuffer[i] is EntityAlive e) || e.IsDead()) continue;
                if (e.sleepingOrWakingUp) continue;   // ignore dormant POI sleepers behind walls
                if ((e.position - pos).sqrMagnitude <= maxSq)
                {
                    armed = true;
                    if (DBG) Dbg($"pod {theEntity.entityId} ARMED by {e.entityId} '{e.EntityClass?.entityClassName}' dist={(e.position - pos).magnitude:0.0}");
                    theEntity.Buffs.AddBuff(armBuff, -1, false, false, -1f);
                    break;
                }
            }
        }

        // Der erste Scan ist durch: ab jetzt darf gefolgt werden.
        readyChecked = true;

        // PHASE 4 - scharf: stehen bleiben. buffSeekerClusterArm laeuft 0.75s und burstet dann.
        // Wer waehrenddessen weiterlaeuft, verstreut seine Kinder irgendwo zwischen Landeort und
        // Spieler - genau das sah man als "splittet sich auf dem Rueckweg". Ausserdem ist
        // `self.position` beim Burst so garantiert ein ruhiger, gegroundeter Punkt.
        if (armed)
        {
            theEntity.moveHelper.Stop();
            DbgTick("ARMED/Stop");
            return;
        }

        // PHASE 5 - Follow the player via real A* pathfinding (routes around obstacles), then
        // HOLD once close. NOT the raw moveHelper.SetMoveTo (dumb straight-line push -> drove off
        // / clipped through houses). Gated like the vanilla task so the pathfinder isn't spammed.
        EntityPlayer p = theEntity.world.GetPrimaryPlayer();
        if (p == null) { theEntity.moveHelper.Stop(); return; }
        float d = Vector3.Distance(p.position, theEntity.position);

        // Bewegung über den gemeinsamen Helper: A* wenn möglich, sonst direktes Schieben.
        // Ohne diesen Fallback stand der Pod komplett still (CanNavigatePath() dauerhaft false,
        // weil onGround nie true wird) -- siehe SeekerMove.cs.
        // ANFAHRT MIT HYSTERESE UND HALTEPUNKT (2026-07-25).
        //
        // Beobachtung des Spielers: im Follow raste der Pod auf den Spieler zu, flog DURCH ihn
        // hindurch, fiel in den Boden und kreiste dann spiralfoermig und leicht steigend um ihn.
        // Auffaellig: genau die Pods, die NIE in den Follow gingen (weil Gegner in der Naehe
        // waren), blieben stabil. Es ist also kein Kapsel-, sondern ein Anfahrtsproblem.
        //
        // Zwei Konstruktionsfehler steckten hier:
        //  (a) Ziel war `p.position`, also der Spieler SELBST. Der Pod bremste damit nie ab - er
        //      fuhr mit voller Geschwindigkeit auf den Mittelpunkt zu und wurde erst durch den
        //      `d > followDist`-Test gestoppt. `Entity.motion` wird aber nur gedaempft
        //      (`motion *= 0.546` pro Tick), nicht genullt: der Pod schoss ueber den Spieler
        //      hinaus. Jetzt ist das Ziel ein Punkt IM Abstand `followDist` auf der Verbindungs-
        //      linie - der Pod faehrt einen Halteplatz an statt den Spieler.
        //  (b) Ein einziger Schwellwert fuer Fahren UND Halten laesst den Zustand bei d ≈
        //      followDist jeden Tick umschlagen (fahren -> Stop -> fahren ...). Zusammen mit dem
        //      Ueberschwingen ergibt das genau das beobachtete Umkreisen. Deshalb jetzt eine
        //      Hysterese: losfahren erst ab `followDist + followHysteresis`, halten bis
        //      `followDist`.
        string action;
        if (following) following = d > followDist;
        else following = d > followDist + followHysteresis;

        if (following)
        {
            // Haltepunkt statt Spielermittelpunkt: `followDist` vor dem Spieler auf unserer Seite.
            Vector3 toPod = theEntity.position - p.position;
            toPod.y = 0f;
            Vector3 goal = toPod.sqrMagnitude > 0.01f
                ? p.position + toPod.normalized * followDist
                : p.position;
            action = SeekerMove.DriveTo(theEntity, goal, this);
        }
        else
        {
            theEntity.moveHelper.Stop();
            action = "HOLD/Stop";
        }

        if (DBG && Time.time >= nextDbgTime)
        {
            nextDbgTime = Time.time + 1f;

            // Physik-Diagnose: onGround wird in Entity.ccEntityCollisionResults aus
            // m_characterController.IsGrounded() gesetzt (== collisionFlags & Below). Wir loggen
            // die Rohwerte, um zu sehen WARUM das nie true wird (CC fehlt? Kapsel degeneriert?
            // Kollision meldet gar nichts?).
            CharacterControllerAbstract cc = theEntity.m_characterController;
            string ccInfo = cc == null
                ? "cc=NULL"
                : $"cc=ok grounded={cc.IsGrounded()} h={cc.GetHeight():0.000} r={cc.GetRadius():0.000}";
            GamePath.PathNavigate nav = theEntity.getNavigator();
            string navInfo = nav == null
                ? "nav=NULL"
                : $"hasPath={nav.HasPath()} planning={nav.isPlanningPath()}";

            Dbg($"pod {theEntity.entityId} dist={d:0.0} m/s={accumTravel:0.00} maxStep={maxStep:0.000} " +
                // `run=` ist der Schluessel zur Follow-Uebergeschwindigkeit: rennend ist die
                // Endgeschwindigkeit 5.83x hoeher als gehend (siehe Patch_SeekerSpeedGovernor).
                // Steht hier im Follow `run=True` und in den ruhigen Phasen `run=False`, ist der
                // Bewegungszustand die Ursache und nicht die Kapsel.
                $"run={theEntity.MovementRunning} following={following} " +
                $"speedAggro={theEntity.GetMoveSpeedAggro():0.00} action={action} armed={armed} landed={hasLanded} " +
                $"onGround={theEntity.onGround} isSwimming={theEntity.isSwimming} " +
                $"canNav={theEntity.CanNavigatePath()} {ccInfo} {navInfo} " +
                $"collFlags={theEntity.collisionFlags} collVert={theEntity.isCollidedVertically} " +
                $"collisionCalls={Patch_SeekerPhysics.GetCollisionCalls(theEntity.entityId)} " +
                $"{SeekerMove.GetPathStats(theEntity.entityId)} " +
                $"clampHits={Patch_SeekerFallClamp.GetClampHits(theEntity.entityId)} " +
                $"motion={theEntity.motion} pos={theEntity.position} playerPos={p.position} " +
                $"| {Patch_SeekerPhysics.GetCapsuleInfo(theEntity.entityId)}");
            accumTravel = 0f; maxStep = 0f;
        }
    }
}
