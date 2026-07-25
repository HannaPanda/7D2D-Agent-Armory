using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// Custom AI task for the CHILD seekers.
//   SeekerDetonate,SeekerCluster class=EntityEnemy,60;detonate_dist=2.2;acquire_range=30;lifetime=30
// Resolved via EAIManager.GetType -> Type.GetType("EAI"+name) (assembly-qualified, global namespace).
//
// Reuses EAIApproachAndAttackTarget for chase/pathing, and adds:
//  (a) self target acquisition (the vanilla animal senses are player-centric and won't
//      reliably see zombies at range) — scan for the nearest EntityEnemy and SetAttackTarget.
//  (b) target SPREADING: prefer a target that fewer sibling seekers are already chasing,
//      so the cluster fans out instead of dogpiling one zombie.
//  (c) detonation on contact: apply the AoE buff, then remove the entity cleanly (no corpse).
//  (d) a lifetime: if it hasn't detonated within `lifetime` seconds (no reachable target),
//      it self-destructs quietly — no explosion, no corpse.
public class EAISeekerDetonate : EAIApproachAndAttackTarget
{
    private float detonateDist = 2.2f;
    private float acquireRange = 30f;
    private float leashRange = 24f;   // stop chasing once a target flees this far (map-chase guard)
    private float lifetime = 30f;
    private float spreadBias = 4f;
    private string detonateBuff = "buffSeekerDetonate";
    private string armBuff = "buffSeekerChildArm";

    private bool fired;
    private float bornTime = -1f;
    private float removeAtTime = -1f;
    private bool registered;
    private string lastMoveHow = "-";   // was SeekerMove.DriveTo zuletzt getan hat (Debug)

    // Reachability tracking. Seekers roll and cannot jump, so a zombie on a ledge/roof is
    // unreachable and — with sticky targeting — would tie up the seeker until its lifetime
    // expires. If we fail to close distance on the current target for `unreachableTimeout`
    // seconds, we blacklist it for a while and let AcquireTarget pick a reachable zombie.
    private float bestDistToTarget = float.MaxValue;
    private float lastProgressTime = -1f;
    private int trackedTargetId = -1;
    private const float unreachableTimeout = 3.0f;   // no progress for this long -> give up
    private const float progressEpsilon = 0.3f;      // meters of closing that counts as progress
    private const float blacklistDuration = 8f;       // how long a target stays written off
    private readonly Dictionary<int, float> unreachable = new Dictionary<int, float>();

    private readonly List<Entity> scanBuffer = new List<Entity>();

    // Live seekers, so each can see what the others are targeting (spread logic).
    private static readonly List<EAISeekerDetonate> Active = new List<EAISeekerDetonate>();

    public override void SetData(Dictionary<string, string> _data)
    {
        base.SetData(_data);
        ParseFloat(_data, "detonate_dist", ref detonateDist);
        ParseFloat(_data, "acquire_range", ref acquireRange);
        ParseFloat(_data, "leash_range", ref leashRange);
        ParseFloat(_data, "lifetime", ref lifetime);
        ParseFloat(_data, "spread_bias", ref spreadBias);
        if (_data.TryGetValue("detonate_buff", out string b)) detonateBuff = b;
        if (_data.TryGetValue("arm_buff", out string a)) armBuff = a;
    }

    private static void ParseFloat(Dictionary<string, string> d, string key, ref float field)
    {
        if (d.TryGetValue(key, out string s))
            float.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out field);
    }

    // Base Start() dereferences entityTarget (set by base.CanExecute, which we no longer call)
    // -> NRE. We need none of its setup (we drive movement/detonation ourselves), so no-op it.
    public override void Start() { }

    // ==== DIE URSACHE DES "FÄHRT ZIELLOS HERUM UND TUNNELT" (gefunden 2026-07-25) ====
    //
    // `EAIApproachAndAttackTarget.Continue()` prüft laut IL unter anderem:
    //     EntityAlive t = theEntity.GetAttackTarget();
    //     if (t == null) return false;                       // IL_0055
    //     if (t != this.entityTarget) return false;          // IL_005F   <<<<
    //     ...
    // Das protected Feld `entityTarget` wird AUSSCHLIESSLICH von `base.CanExecute()` gesetzt -
    // und genau die rufen wir seit dem Umbau auf eigenes Homing nicht mehr auf. `entityTarget`
    // bleibt also permanent null, und damit lieferte `Continue()` in JEDEM Tick false.
    //
    // `EAITaskList` behandelt einen ausführenden Task mit `Continue() == false` als beendet und
    // ruft `Reset()`. Unser Reset ruft `base.Reset()`, und das macht laut IL:
    //     theEntity.IsEating = false;
    //     theEntity.moveHelper.Stop();     <<<< und Stop() ruft intern navigator.clearPath()
    //     blockTargetTask.canExecute = false;
    //
    // Der Ablauf pro Tick war damit: CanExecute -> Ziel setzen -> Update -> FindPath/SetMoveTo
    // -> Continue false -> Reset -> Stop + clearPath. Jede Bewegungsanweisung wurde also im
    // selben Tick wieder eingerissen. Das erklärt lückenlos:
    //   * `hasPath=False` in 48 von 50 Stichproben bei gleichzeitig `pathArrived >= 1` -
    //     ankommende Routen wurden sofort per clearPath() zerstört;
    //   * warum der grobe Direktschub die dominante Bewegungsart war (nie eine lebende Route);
    //   * das ruckelige Fahren in wechselnde Richtungen und das Tunneln durch Wände;
    //   * die dauernde Register/Unregister-Kette auf der statischen Active-Liste, die die
    //     Ziel-Verteilung unter Geschwistern unbrauchbar machte.
    //
    // Wir steuern Bewegung, Zielwahl und Lebenszeit komplett selbst, also ist die richtige
    // Antwort dieselbe wie bei Start(): die Basisimplementierung ersetzen. Continue() spiegelt
    // CanExecute(), damit Lebenszeit- und Zielprüfung weiterhin jeden Tick laufen.
    //
    // MERKSATZ: Wer `CanExecute()` überschreibt, ohne `base.CanExecute()` aufzurufen, MUSS
    // `Start()` UND `Continue()` mitüberschreiben - sonst arbeiten die Basismethoden auf einem
    // nie befüllten `entityTarget`.
    public override bool Continue()
    {
        return CanExecute();
    }

    public override bool CanExecute()
    {
        if (theEntity == null) return false;
        if (bornTime < 0f) bornTime = Time.time;
        // Lifetime self-destruct MUST live here, not only in Update(): a targetless seeker
        // made base.CanExecute() false, so Update() never ran and the old lifetime check
        // never fired -> idle seekers lingered forever (log showed one alive ~54s at 10s life).
        if (!fired && Time.time - bornTime >= lifetime) { Remove(); return false; }
        AcquireTarget();
        return true;   // always run: our Update drives homing + detonation + cleanup
    }

    // Pick the nearest live EntityEnemy, biased AWAY from targets siblings already chase.
    private void AcquireTarget()
    {
        if (theEntity == null || theEntity.IsDead()) return;

        EntityAlive current = theEntity.GetAttackTarget();
        // STICKY targeting: once we have a live target, commit to it. Re-evaluating every
        // frame (peeling off whenever a sibling shares the target) made the whole cluster
        // thrash — constantly swapping targets resets pathing, so seekers crawled, ran off
        // toward a far "lonelier" zombie, or stalled. The spread is applied ONCE here, at
        // acquisition; after that we chase our pick to the death. With a single enemy this
        // naturally lets all seekers converge on it (there is no other choice).
        // Exception: if the current target got written off as unreachable, drop it and re-pick.
        if (current != null && !current.IsDead() && !IsUnreachable(current.entityId)) return;

        World world = theEntity.world;
        if (world == null) return;

        Vector3 pos = theEntity.position;
        Bounds bb = new Bounds(pos, new Vector3(acquireRange * 2f, acquireRange * 2f, acquireRange * 2f));
        scanBuffer.Clear();
        world.GetEntitiesInBounds(typeof(EntityEnemy), bb, scanBuffer);

        EntityAlive best = null;
        float bestScore = float.MaxValue;
        float maxSq = acquireRange * acquireRange;
        for (int i = 0; i < scanBuffer.Count; i++)
        {
            if (!(scanBuffer[i] is EntityAlive e) || e == theEntity || e.IsDead()) continue;
            if (e.sleepingOrWakingUp) continue;        // ignore dormant POI sleepers (spawned but
                                                        // untriggered/invisible -> would send seekers
                                                        // charging at zombies behind unopened walls)
            if (IsUnreachable(e.entityId)) continue;   // skip zombies we can't get to
            float dsq = (e.position - pos).sqrMagnitude;
            if (dsq > maxSq) continue;
            // Nearer is better; each sibling already on this target multiplies the cost.
            float score = dsq * (1f + spreadBias * SiblingsOn(e.entityId));
            if (score < bestScore)
            {
                bestScore = score;
                best = e;
            }
        }

        if (best != null && best != current)
        {
            theEntity.SetAttackTarget(best, 200);
            trackedTargetId = -1;   // restart progress tracking for the new target
            if (DBG)
            {
                float d = (best.position - pos).magnitude;
                Dbg($"seeker {theEntity.entityId} -> target {best.entityId} '{best.EntityClass?.entityClassName}' " +
                    $"dist={d:0.0} siblings={SiblingsOn(best.entityId)} candidates={scanBuffer.Count}");
            }
        }
        else if (best == null && DBG && scanBuffer.Count > 0 && Time.time >= nextNoTargetLog)
        {
            // Only interesting when there WERE candidates but all got filtered (sleeping/
            // unreachable). "scanned 0" = simply nothing nearby, not worth spamming.
            //
            // GEDROSSELT (2026-07-25): diese eine Zeile stellte 235 von 339 [SeekerDbg]-Zeilen
            // im Log. Sie wird pro Tick ausgegeben, solange kein Ziel passt - und "kein
            // passendes Ziel" ist beim Testen der NORMALFALL, weil selten genug Zombies für alle
            // 5 Kinder da sind. Ein Seeker ohne Ziel hält einfach still, bis seine lifetime ihn
            // entfernt; das ist kein Fehler und muss nicht 20x/s protokolliert werden.
            nextNoTargetLog = Time.time + 2f;
            Dbg($"seeker {theEntity.entityId} found NO valid target ({scanBuffer.Count} nearby but all sleeping/unreachable)");
        }
    }

    private float nextNoTargetLog = -1f;   // Drossel für die "kein Ziel"-Meldung (pro Instanz)
    private float nextMoveLog = -1f;       // Drossel für das MOVE-Statuslog (pro Instanz)

    // GESCHWINDIGKEITSMESSUNG (2026-07-25). Die erste Fassung mass die Strecke zwischen zwei
    // LOG-Zeitpunkten (1s Abstand) - unbrauchbar, weil ein RESCUE-Teleport oder ein
    // Origin-Shift als "zurückgelegte Strecke" mitzählte (max 30.78 m/s im letzten Log war
    // genau so ein Artefakt). Jetzt wird PRO TICK gemessen und alles über
    // `teleportStep` als Sprung verworfen; aufsummiert ergibt das die echte Strecke pro
    // Sekunde, und `maxStep` zeigt den grössten sauberen Einzelschritt.
    private Vector3? lastTickPos;
    private float accumTravel;
    private float maxStep;
    private const float teleportStep = 2f;   // > 2m in einem Tick = kein Laufen, sondern Sprung

    // ZUSATZMESSUNG (2026-07-25 d) - "zu schnell" ODER "springt"?
    //
    // Befund Log `..._13-11-26`: `m/s=0.00` in 123 von 191 Zeilen, und `how=noNav+waitPath` in
    // exakt denselben 123. Die Drohnen stehen also die meiste Zeit STILL und legen den Rest in
    // Schueben zurueck (Median 6.87 m/s in den bewegten Sekunden, Einzelschritt-Median 1.389 m).
    // Eine Sekunde mit 6.87 m Strecke, aber Einzelschritten von 1.4 m, besteht aus ~5 Schritten -
    // das ist keine Fortbewegung mit 2.7 m/s, das sind Sprünge.
    //
    // Was diese Felder unterscheiden sollen:
    //   `ticks`  - wie oft diese Instanz pro Sekunde ueberhaupt lief. Ohne diese Zahl ist jeder
    //              Schritt uninterpretierbar: 1.4 m sind bei 20 Ticks/s etwas voellig anderes als
    //              bei 3 Ticks/s. Genau diese Bezugsgroesse hat bisher gefehlt.
    //   `jumps`  - Schritte ueber `jumpStep`. Locomotion mit 2.7 m/s liegt bei 0.135 m/Tick; alles
    //              ueber 0.5 m kann keine Fortbewegung sein, egal bei welcher Tickrate.
    //   `rawMax` - groesster Schritt OHNE Teleportfilter. Klafft `rawMax` weit ueber `maxStep`,
    //              werden echte Spruenge bisher nur weggefiltert statt erklaert.
    //   `spdFwd` - was die Engine selbst als Vorwaertsgeschwindigkeit fuehrt. Ist die klein,
    //              waehrend die Position springt, kommt die Verschiebung NICHT aus der Lokomotion.
    private int tickCount;
    private int jumpCount;
    private float rawMaxStep;
    private const float jumpStep = 0.5f;

    // ORIGIN-SHIFT-ERKENNUNG (2026-07-25 e).
    //
    // Log `..._14-01-03` zeigte `rawMax=59.57` - ein Einzelschritt von 59 Metern. Das ist keine
    // Physik, das ist ein Sprung. Der wahrscheinlichste Kandidat ist das Origin-Shifting: 7DTD
    // verschiebt periodisch den Weltursprung, und `Entity.position` wird als
    // `PhysicsTransform.position + Origin.position` gebildet. Verschiebt sich der Ursprung
    // zwischen zwei Messungen, sieht unsere Differenzmessung eine riesige Scheinbewegung, obwohl
    // die Drohne in Weltkoordinaten stillsteht.
    // Statt weiter zu raten wird der Ursprung mitgemessen: `oshift` zaehlt die Ticks, in denen er
    // sich geaendert hat. Ist `oshift` > 0 genau dort, wo `rawMax` explodiert, sind die grossen
    // Spruenge Messartefakte und KEIN Bewegungsfehler - dann ist nur noch die Sprungkomponente im
    // Bereich ~1.4 m echt. Bleibt `oshift` bei 0, ist der Sprung real und muss anderswo herkommen.
    private Vector3? lastOrigin;
    private int originShifts;

    private const bool DBG = true;   // flip to false to silence [SeekerDbg] logging
    private static void Dbg(string s) { if (DBG) UnityEngine.Debug.Log("[SeekerDbg] " + s); }

    // A target is "unreachable" only for a cooldown window, then we allow a retry (it may
    // have moved somewhere we can reach). Expired entries are pruned on access.
    private bool IsUnreachable(int id)
    {
        if (unreachable.TryGetValue(id, out float until))
        {
            if (Time.time < until) return true;
            unreachable.Remove(id);
        }
        return false;
    }

    private int SiblingsOn(int targetId)
    {
        int c = 0;
        for (int i = 0; i < Active.Count; i++)
        {
            EAISeekerDetonate s = Active[i];
            if (s == this || s.theEntity == null || s.theEntity.IsDead() || s.theEntity.IsMarkedForUnload())
                continue;
            EntityAlive t = s.theEntity.GetAttackTarget();
            if (t != null && t.entityId == targetId) c++;
        }
        return c;
    }

    public override void Update()
    {
        // Deliberately NOT calling base.Update(): EAIApproachAndAttackTarget's movement flanks
        // the target (GetMoveToLocation + randomized offsets = the zombie "surround" behavior).
        // On a fast seeker that reads as insane orbiting + wall clipping. We drive straight-line
        // homing ourselves via moveHelper.SetMoveTo instead.
        if (theEntity == null) return;
        Register();

        if (fired)
        {
            if (Time.time >= removeAtTime) Remove();
            return;
        }
        if (theEntity.IsDead()) { Remove(); return; }

        EntityAlive tgt = theEntity.GetAttackTarget();
        if (tgt == null || tgt.IsDead()) { theEntity.moveHelper.Stop(); return; }

        float dsq = (tgt.position - theEntity.position).sqrMagnitude;
        float distToTgt = Mathf.Sqrt(dsq);

        // LEASH: acquire_range only bounds the INITIAL pick. Without this a locked target that
        // runs (esp. fast zombie dogs) gets chased across the map. Drop it past leashRange.
        if (distToTgt > leashRange)
        {
            if (DBG) Dbg($"seeker {theEntity.entityId} LEASH drops {tgt.entityId} '{tgt.EntityClass?.entityClassName}' at dist={distToTgt:0.0}");
            theEntity.SetAttackTarget(null, 0);
            theEntity.moveHelper.Stop();
            trackedTargetId = -1; bestDistToTarget = float.MaxValue;
            return;
        }

        // DETONATE on contact — unless still in the brief post-spawn arm fuse.
        bool arming = !string.IsNullOrEmpty(armBuff) && theEntity.Buffs.HasBuff(armBuff);
        if (!arming && dsq <= detonateDist * detonateDist)
        {
            fired = true;
            if (DBG) Dbg($"seeker {theEntity.entityId} DETONATE on {tgt.entityId} '{tgt.EntityClass?.entityClassName}' dist={distToTgt:0.0}");
            theEntity.moveHelper.Stop();
            // AoE only (no self health change). The seeker is explosion-immune; we MarkToUnload
            // a beat later -> the blast lands but leaves no corpse.
            theEntity.Buffs.AddBuff(detonateBuff, -1, false, false, -1f);
            removeAtTime = Time.time + 0.1f;
            return;
        }

        // REACHABILITY: if we stop closing distance for `unreachableTimeout`, the target is
        // unreachable (can't jump — ledge/roof/wall). Blacklist it and drop it so AcquireTarget
        // picks a reachable one. If none reachable, lifetime (checked in CanExecute) removes us.
        if (trackedTargetId != tgt.entityId)
        {
            trackedTargetId = tgt.entityId;
            bestDistToTarget = distToTgt;
            lastProgressTime = Time.time;
        }
        else if (distToTgt < bestDistToTarget - progressEpsilon)
        {
            bestDistToTarget = distToTgt;
            lastProgressTime = Time.time;
        }
        else if (Time.time - lastProgressTime > unreachableTimeout)
        {
            // Bewegungs-Rohwerte mitloggen: so ist sofort unterscheidbar, ob das Ziel wirklich
            // unerreichbar ist (Vorsprung/Dach) oder ob die Drohne sich schlicht nicht bewegt
            // (onGround=false -> kein Pathing) -- das war die Ursache der Blacklist-Kaskade.
            if (DBG) Dbg($"seeker {theEntity.entityId} DROPS unreachable target {tgt.entityId} " +
                         $"'{tgt.EntityClass?.entityClassName}' (stuck at dist={distToTgt:0.0} for {unreachableTimeout}s) " +
                         $"move={lastMoveHow} onGround={theEntity.onGround} canNav={theEntity.CanNavigatePath()}");
            unreachable[tgt.entityId] = Time.time + blacklistDuration;
            theEntity.SetAttackTarget(null, 0);
            theEntity.moveHelper.Stop();
            trackedTargetId = -1; bestDistToTarget = float.MaxValue;
            return;
        }

        // HOME at the target: real A* when the navigation gate allows it, otherwise a direct
        // push so the seeker still closes in. Previously this was FindPath ONLY, gated by
        // CanNavigatePath() -- which is permanently false for our drones (onGround never becomes
        // true), so the seekers never moved at all and then blacklisted every target as
        // "unreachable". See SeekerMove.cs for the full evidence.
        lastMoveHow = SeekerMove.DriveTo(theEntity, tgt.position, this);

        // KREIS-DIAGNOSE (2026-07-25): der Pod hat seit Tagen ein gedrosseltes Statuslog, die
        // KINDER haben keins - und genau sie sind die Mehrheit (60 von 90 Entities). "Kreisen"
        // liess sich deshalb bisher nie einem konkreten Seeker zuordnen.
        // Entscheidend ist die Kombination aus Distanz und zurueckgelegtem Weg: schrumpft die
        // Distanz zum Ziel nicht, waehrend die Drohne trotzdem Strecke macht, faehrt sie im
        // Kreis. Genau dieses Paar wird hier pro Sekunde und pro Instanz protokolliert.
        // Pro Tick messen (siehe Kommentar an lastTickPos), Sprünge verwerfen.
        Vector3 nowPos = theEntity.position;
        tickCount++;

        // Ursprungsverschiebung erkennen und den Tick von der Streckenmessung ausnehmen -
        // sonst zaehlt ein Origin-Shift als mehrere Dutzend Meter "Bewegung".
        Vector3 nowOrigin = Origin.position;
        bool originMoved = lastOrigin.HasValue && nowOrigin != lastOrigin.Value;
        if (originMoved) originShifts++;
        lastOrigin = nowOrigin;

        // Bewusst KEIN early return: die Log-Zeile dieses Ticks soll trotzdem erscheinen,
        // nur die Streckenmessung wird uebersprungen.
        if (lastTickPos.HasValue && !originMoved)
        {
            float step = Vector3.Distance(nowPos, lastTickPos.Value);
            if (step > rawMaxStep) rawMaxStep = step;      // OHNE Filter - zeigt auch Teleports
            if (step > jumpStep) jumpCount++;
            if (step <= teleportStep)
            {
                accumTravel += step;
                if (step > maxStep) maxStep = step;
            }
        }
        lastTickPos = nowPos;

        if (DBG && Time.time >= nextMoveLog)
        {
            nextMoveLog = Time.time + 1f;
            GamePath.PathNavigate nav = theEntity.getNavigator();
            // speedAggro = was die Entity laut Stats DARF, m/s = was sie TATSAECHLICH tut.
            // Klaffen die auseinander, liegt es nicht am Pathing, sondern an der Ausfuehrung
            // der Bewegung (oder daran, dass ein anderer Mod die Laufgeschwindigkeit hochsetzt:
            // GetMoveSpeedAggro laeuft durch EffectManager.GetValue mit PassiveEffects
            // WalkSpeed/RunSpeed, ist also von aussen modifizierbar).
            Dbg($"seeker {theEntity.entityId} MOVE tgt={tgt.entityId} dist={distToTgt:0.0} " +
                $"best={bestDistToTarget:0.0} m/s={accumTravel:0.00} maxStep={maxStep:0.000} " +
                // ticks/jumps/rawMax/spdFwd: trennen "laeuft zu schnell" von "springt".
                // Erwartung bei sauberer Lokomotion: ticks ~20, jumps=0, rawMax ~ maxStep ~ 0.135,
                // spdFwd ~ speedAggro. Jede Abweichung zeigt, WELCHE der beiden Ursachen vorliegt.
                $"ticks={tickCount} jumps={jumpCount} rawMax={rawMaxStep:0.000} " +
                // oshift>0 an derselben Stelle wie ein grosses rawMax => der Sprung ist ein
                // Messartefakt des Origin-Shiftings, kein Bewegungsfehler.
                $"oshift={originShifts} spdFwd={theEntity.speedForward:0.00} " +
                // Zeigt, wie oft und wie weit der harte Begrenzer eingreifen musste. Hohe Werte
                // heissen: die unbekannte Quelle feuert weiterhin, wird aber gedeckelt.
                $"{Patch_SeekerStepLimiter.GetStats(theEntity.entityId)} " +
                $"speedAggro={theEntity.GetMoveSpeedAggro():0.00} how={lastMoveHow} " +
                $"onGround={theEntity.onGround} canNav={theEntity.CanNavigatePath()} " +
                $"hasPath={(nav != null && nav.HasPath())} {SeekerMove.GetPathStats(theEntity.entityId)}");
            accumTravel = 0f; maxStep = 0f;
            tickCount = 0; jumpCount = 0; rawMaxStep = 0f; originShifts = 0;
        }
    }

    private void Register()
    {
        if (registered) return;
        // Prune any dead/unloaded seekers so the static list stays bounded across throws.
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            EAISeekerDetonate s = Active[i];
            if (s.theEntity == null || s.theEntity.IsDead() || s.theEntity.IsMarkedForUnload())
            {
                s.registered = false;
                Active.RemoveAt(i);
            }
        }
        Active.Add(this);
        registered = true;
    }

    private void Unregister()
    {
        if (!registered) return;
        Active.Remove(this);
        registered = false;
    }

    // Corpse-free removal: MarkToUnload is the normal despawn path (no ragdoll/lootable body).
    private void Remove()
    {
        Unregister();
        if (theEntity != null && !theEntity.IsMarkedForUnload())
            theEntity.MarkToUnload();
    }

    public override void Reset()
    {
        base.Reset();
        Unregister();
    }
}
