using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

// HARTER WEG-PRO-TICK-BEGRENZER (2026-07-25).
//
// WARUM DIESER PATCH UEBERHAUPT EXISTIERT - was vorher geprueft und AUSGESCHLOSSEN wurde:
//  * Kollisionskapsel: nachweislich korrekt (`capsulePREFAB-OK`, r=0.309/h=0.727 beim Pod,
//    r=0.17/h=0.40 beim Kind, `r/hHalf=0.85`). Kein Entartungsfall mehr.
//  * Origin-Shifting: `oshift=0` in ALLEN 162 MOVE-Zeilen des Logs `..._14-15-51`. Die Spruenge
//    sind also echt und kein Messartefakt.
//  * Bewegungszustand: `run=False` auch in den Zeilen mit `m/s=18.05`. Die Rennen/Gehen-
//    Unterscheidung (Faktor 5.83) ist NICHT die Ursache.
//  * Basisklasse: `EAIApproachAndAttackTarget.Update` enthaelt zwar ein `SetPosition`, wir rufen
//    `base.Update()` aber bewusst nicht auf.
//  * Rettungsnetz: dessen Teleports sind rein vertikal (~3.7 m), nicht die beobachteten Weiten.
//  * Roll-Visual: schreibt nur `model.localPosition`, kann `Entity.position` nicht bewegen.
//  * Geschwindigkeits-Governor ueber `GetPassiveEffectSpeedModifier`: wirkungslos, weil er den
//    `Entity.Move`-Pfad regelt. Im Log steht bei `m/s=18.05` gleichzeitig
//    `motion=(0.00,-0.08,0.00)` - die horizontale Verschiebung kommt dort nicht her.
//
// Gemessen wurden Einzelschritte bis **72.68 m pro Tick** bei `speedAggro=2.20`. Ein Schritt, der
// ein Vielfaches des Kapselradius betraegt, springt zwangslaeufig ueber Waende hinweg, bevor eine
// Kollision aufgeloest werden kann - das ist das gemeldete Tunneln, und es ist unabhaengig davon,
// WELCHE Codestelle die Position setzt.
//
// Dieser Patch ist bewusst ein GUARD, keine Ursachenbehebung: er begrenzt die Verschiebung pro
// Tick auf das, was die XML-Geschwindigkeit hergibt. Damit ist das Symptom (Rasen, Ueberschwingen,
// Wanddurchschlaege) unabhaengig von der noch unbekannten Quelle gedeckelt, und das Feld
// `stepLimited` zeigt, WIE OFT und WIE WEIT eingegriffen werden musste - also genau die Statistik,
// die die Quelle als naechstes eingrenzt.
//
// WICHTIG: Der Begrenzer laeuft als Postfix auf `EntityAlive.OnUpdateEntity` mit
// `Priority.Last`, also NACH allem anderen (inkl. unserem Rettungsnetz). Gewollte Teleports muss
// er deshalb durchlassen - dafuer meldet das Rettungsnetz seinen Sprung ueber `AllowTeleport(id)`
// an, und der naechste Tick wird einmalig nicht begrenzt.
[HarmonyPatch(typeof(EntityAlive), "OnUpdateEntity")]
public static class Patch_SeekerStepLimiter
{
    // Toleranz auf die erlaubte Strecke pro Tick. 1.5x laesst normale Beschleunigung, Haenge und
    // Stufen unangetastet und greift erst bei echten Ausreissern.
    private const float Tolerance = 1.5f;
    // Tickrate der aktiven Entities - im Log gemessen (`ticks=20` bei allen bewegten Instanzen).
    private const float TicksPerSecond = 20f;

    private static readonly Dictionary<int, Vector3> lastPos = new Dictionary<int, Vector3>();
    private static readonly HashSet<int> teleportGrace = new HashSet<int>();

    // Statistik fuer die Diagnose: wie oft und wie weit musste gedeckelt werden.
    private static readonly Dictionary<int, int> limitHits = new Dictionary<int, int>();
    private static readonly Dictionary<int, float> worstStep = new Dictionary<int, float>();
    // Erlaubter Schritt im Verhaeltnis zum Kapselradius - der eigentliche Grenzwert fuer das Tempo.
    private static readonly Dictionary<int, float> budgetRatio = new Dictionary<int, float>();

    public static void AllowTeleport(int id) { teleportGrace.Add(id); }

    public static string GetStats(int id)
    {
        limitHits.TryGetValue(id, out int n);
        worstStep.TryGetValue(id, out float w);
        budgetRatio.TryGetValue(id, out float br);
        // `budget` = erlaubter Schritt / Kapselradius. DAS ist die Zahl, an der die
        // Geschwindigkeit haengt: bleibt sie um 1.2, ist Tunneln erfahrungsgemaess kein Thema;
        // deutlich darueber springt die Drohne ueber duenne Geometrie. Wer das Tempo erhoehen
        // will, muss entweder hier Luft haben oder den Collider vergroessern.
        return $"stepLimited={n} worstStep={w:0.000} budget={br:0.00}x";
    }

    public static void ForgetEntity(int id)
    {
        lastPos.Remove(id);
        teleportGrace.Remove(id);
        limitHits.Remove(id);
        worstStep.Remove(id);
        budgetRatio.Remove(id);
    }

    [HarmonyPriority(Priority.Last)]
    static void Postfix(EntityAlive __instance)
    {
        if (!Patch_SeekerPhysics.IsSeeker(__instance)) return;

        int id = __instance.entityId;
        Vector3 now = __instance.position;

        if (!lastPos.TryGetValue(id, out Vector3 prev)) { lastPos[id] = now; return; }

        // Einmalige Gnade nach einem gewollten Teleport (Rettungsnetz).
        if (teleportGrace.Remove(id)) { lastPos[id] = now; return; }

        Vector3 delta = now - prev;
        float dist = delta.magnitude;

        // Erlaubte Strecke aus der XML-Geschwindigkeit ableiten, damit MoveSpeedAggro wieder
        // spuerbar ist - genau das, was der Governor erreichen sollte, nur an der Stelle, an der
        // die Verschiebung tatsaechlich ankommt.
        float speed = __instance.GetMoveSpeedAggro();
        if (speed <= 0f) speed = 2.5f;
        float maxPerTick = speed / TicksPerSecond * Tolerance;

        // Verhaeltnis zum Kapselradius einmal pro Tick mitfuehren (Diagnose, siehe GetStats).
        CharacterControllerAbstract cc = __instance.m_characterController;
        if (cc != null)
        {
            float rad = cc.GetRadius();
            if (rad > 0.001f) budgetRatio[id] = maxPerTick / rad;
        }

        if (dist <= maxPerTick || dist <= 0.0001f) { lastPos[id] = now; return; }

        worstStep.TryGetValue(id, out float w);
        if (dist > w) worstStep[id] = dist;
        limitHits.TryGetValue(id, out int hits);
        limitHits[id] = hits + 1;

        // Auf die erlaubte Strecke zuruecksetzen, Richtung beibehalten.
        Vector3 clamped = prev + delta / dist * maxPerTick;
        __instance.SetPosition(clamped, true);
        lastPos[id] = clamped;
    }
}
