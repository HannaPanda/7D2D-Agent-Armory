using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

// Physik-Reparatur + Messung für die Seeker-Drohnen.
//
// BEFUND (Log 2026-07-23 11:51): der CharacterController der Drohnen meldete NIE eine
// Kollision -- `collFlags=None` und `grounded=False` in jeder einzelnen Zeile, dazu
// `h=2.100 r=1.050`. Folgen: `onGround` wird nie true -> `CanNavigatePath()` bleibt false ->
// kein A*-Pathing (nur noch der SetMoveTo-Notfallpfad, der geradlinig schiebt) und die
// Drohnen fallen/laufen durch Boden und Wände.
//
// URSACHE (IL-verifiziert in KinematicCharacterMotor.SetCapsuleDimensions):
//   _characterTransformToCapsuleBottomHemi = center - up*(height*0.5) + up*radius
//   _characterTransformToCapsuleTopHemi    = center + up*(height*0.5) - up*radius
// Bei height == 2*radius (exakt unser Fall: 2.100 == 2*1.050) ergeben BEIDE Ausdrücke
// exakt `center` -- die beiden Hemisphären-Mittelpunkte fallen zusammen. Die Kapsel ist zu
// einer entarteten Kugel kollabiert, und die darauf aufbauenden Sweeps/ProbeGround liefern
// keine brauchbaren Treffer mehr.
// Woher kommen die Werte: `Entity.AddCharacterController` übernimmt für Nicht-Spieler
// center/height/radius UNSKALIERT vom CapsuleCollider der Physics-Node ("SeekerCC") aus dem
// Prefab. Dort ist die Kapsel als exakte Kugel um das runde Drohnen-Mesh gebaut.
// Zusätzlich klemmt `ValidateData` radius auf <= height*0.5, weshalb genau der Grenzfall steht.
//
// FIX: Kapsel einmalig pro Entity minimal "entkugeln" -- height auf radius*2.4 anheben, damit
// die Hemisphären wieder getrennt sind. Der Zuwachs ist lokal ~0.15 Einheiten, bei SizeScale
// 0.11-0.2 also ~2-3cm in der Welt: visuell irrelevant, aber der Sweep wird wieder gültig.
// Das ist absichtlich zur Laufzeit gelöst, damit das Unity-Prefab nicht neu gebaut werden muss.
[HarmonyPatch(typeof(EntityAlive), "OnUpdateEntity")]
public static class Patch_SeekerPhysics
{
    private static EntityClass podClass, childClass;
    private static bool resolved;

    // ===== KAPSELMASSE SIND WELTMETER, NICHT PREFAB-EINHEITEN (Korrektur 2026-07-25) =====
    //
    // Der KinematicCharacterMotor ignoriert JEDE Skalierung. IL-Beleg aus
    // `CharacterCollisionsOverlap` (und identisch in den Sweep-Methoden):
    //     bottom = position + rotation * _characterTransformToCapsuleBottomHemi;
    //     top    = position + rotation * _characterTransformToCapsuleTopHemi;
    //     Physics.OverlapCapsuleNonAlloc(bottom, top, Capsule.radius + inflate, ...);
    // Nur `Quaternion * Vector3` - kein `lossyScale`, kein `TransformPoint`. Und `ValidateData`
    // setzt am Ende explizit `transform.localScale = Vector3.one`.
    // ⇒ Was im CapsuleCollider steht, IST die Weltgrösse. `SizeScale` skaliert das Modell, aber
    //   NICHT die Kollisionskapsel.
    //
    // Folge des alten Denkfehlers ("worldR = localR * lossyScale"): die Kinder liefen mit einem
    // Kapselradius von 1.818 METERN herum - eine 3.6m breite Kugel um eine 23cm-Drohne. Vorher
    // waren es 1.0m, also ebenfalls das ~9-fache. Der Motor drueckt eine derart ueberdimensionierte
    // Kapsel jeden Tick aus der umgebenden Geometrie heraus; genau das misst `maxStep` mit einem
    // Median von 1.648 (≈ Radius) bei einem Sollschritt von 2.7/20 = 0.135. Daher `m/s` im Median
    // 8.41 bei `speedAggro=2.70` - die Drohnen liefen nicht schnell, sie wurden herausgeschleudert.
    // Das erklaert Kreisen, "schiesst zum Spieler", Wandklettern und das Durchfallen mit EINER
    // Ursache, und es erklaert, warum der vanilla Rabbit funktioniert: der hat SizeScale ~1, bei
    // ihm sind Prefab-Werte zufaellig schon Weltwerte.
    //
    // Zielwerte direkt in Metern. Sichtbarer Mesh-Radius: Kind ~0.115, Pod ~0.21 (aus
    // `modelOffset deltaLocal=1.049` * SizeScale). Der Collider liegt bewusst etwas darueber,
    // damit Weg-pro-Tick / Radius unter 1 bleibt (Tunnelschwelle; vanilla Zombie 0.32).
    //
    // NUR DER RADIUS IST HIER FREI WAEHLBAR. Hoehe und Center leiten sich zur Laufzeit aus
    // `physicsBaseHeight / physicsHeightScale` ab, weil die Engine genau so rechnet (Begruendung
    // an der Verwendungsstelle weiter unten). Beide Wuensche werden ausserdem auf h/2 gedeckelt:
    //   Kind h=0.304 -> r=0.147, horizontal 2.7 m/s = 0.135/Tick -> 0.92x  (knapp, siehe unten)
    //   Pod  h=0.552 -> r=0.271, horizontal 2.2 m/s = 0.110/Tick -> 0.41x
    // Der Kind-Wert 0.92 liegt unter der Tunnelschwelle, aber ohne viel Reserve; der Radius laesst
    // sich nicht weiter anheben, ohne cY = h/2 aufzugeben. Falls Wanddurchschlaege bleiben, ist
    // MoveSpeed 2.7 -> 2.4 (= 0.82x) der Hebel, nicht der Collider.
    private const float ChildCapsuleRadius = 0.18f;
    private const float PodCapsuleRadius = 0.28f;

    // Entities, deren Kapsel wir schon korrigiert haben.
    private static readonly HashSet<int> fixedCapsule = new HashSet<int>();
    // Diagnose-Text pro Entity (einmal berechnet, vom Pod-Log ausgegeben).
    private static readonly Dictionary<int, string> capsuleInfo = new Dictionary<int, string>();

    // Zählt, wie oft der echte Kollisionspfad (Entity.entityCollision) pro Entity lief.
    // Damit ist unterscheidbar: "Kollision läuft, findet aber nichts" vs. "Kollisionspfad
    // wird gar nicht erst betreten" (dann käme die Bewegung von woanders und umginge die Physik).
    public static readonly Dictionary<int, int> CollisionCalls = new Dictionary<int, int>();

    public static bool IsSeeker(EntityAlive e)
    {
        if (e == null) return false;
        if (!resolved)
        {
            podClass = EntityClass.GetEntityClass(EntityClass.FromString("entitySeekerCluster"));
            childClass = EntityClass.GetEntityClass(EntityClass.FromString("entitySeekerChild"));
            resolved = true;
        }
        EntityClass ec = e.EntityClass;
        return ec != null && (ec == podClass || ec == childClass);
    }

    // Notbremse für die Lebenszeit der KINDER (der Pod begleitet den Spieler und ist bewusst
    // ausgenommen).
    //
    // Die eigentliche lifetime (XML, 10s) liegt in EAISeekerDetonate.CanExecute(). Diese wird
    // aber nur aufgerufen, wenn EAITaskList.isBestTask() den Task überhaupt zulässt -- ein
    // inkompatibler Task mit anderen MutexBits (das war der geerbte Wander) blockiert ihn
    // dauerhaft, und dann verfällt die Lebenszeit still: beobachtet als Seeker, die weit über
    // ihre 10s hinaus ziellos weiterrollten. Der Wander-Erbe ist in entityclasses.xml behoben,
    // aber die Task-Auswahl ist zu fragil, um die Aufräumlogik allein daran zu hängen.
    // Dieser Patch läuft dagegen garantiert jeden Frame, unabhängig von jeder Task-Auswahl.
    // Bewusst grosszügig (30s >> 10s): greift nur, wenn die reguläre Logik versagt hat.
    private const float HardLifetimeSeconds = 30f;
    private static readonly Dictionary<int, float> firstSeen = new Dictionary<int, float>();

    private static void EnforceHardLifetime(EntityAlive e, int id)
    {
        if (e.EntityClass != childClass) return;   // nur Kinder, nicht der Pod
        if (e.IsMarkedForUnload()) return;

        if (!firstSeen.TryGetValue(id, out float t))
        {
            firstSeen[id] = Time.time;
            return;
        }
        if (Time.time - t >= HardLifetimeSeconds)
        {
            Debug.LogWarning($"[SeekerPhysDbg] {id} HARD-LIFETIME erreicht ({HardLifetimeSeconds}s) " +
                             $"- regulaerer Lifetime-Check hat nicht gegriffen, entferne Entity");
            firstSeen.Remove(id);
            e.MarkToUnload();
        }
    }

    public static string GetCapsuleInfo(int id)
    {
        return capsuleInfo.TryGetValue(id, out string s) ? s : "capsule=?";
    }

    public static int GetCollisionCalls(int id)
    {
        return CollisionCalls.TryGetValue(id, out int n) ? n : -1;
    }

    // Aufgeräumt aus Patch_SeekerCleanup (Postfix auf Entity.OnEntityUnload).
    //
    // WARUM DAS NÖTIG IST: `fixedCapsule` ist ein reines "schon erledigt"-Set. Ohne Pruning
    // bleibt eine entityId dort für immer stehen; wird sie später erneut vergeben (z.B. nach
    // einem Save-Reload im selben Prozess), überspringt der Postfix die Kapselreparatur für die
    // NEUE Entity komplett - die behält dann h=2.0 / centerY=0, also die entartete Kapsel, die
    // seinerzeit das ganze Grounding kaputtgemacht hat. Genau die Sorte Fehler, die sich als
    // "manchmal landen sie nicht richtig" äussert.
    public static void ForgetEntity(int id)
    {
        fixedCapsule.Remove(id);
        capsuleInfo.Remove(id);
        firstSeen.Remove(id);
        CollisionCalls.Remove(id);
        maxVertPerTick.Remove(id);
    }

    // Erlaubte Vertikalbewegung pro Tick, PRO ENTITY aus dem echten Weltradius der Kapsel.
    // Eine Entity, die pro Tick mehr als ihren eigenen Radius zurücklegt, springt über dünne
    // Geometrie hinweg, bevor die Kollision aufgelöst werden kann (Messreihe: vanilla Zombie
    // 0.32x = sicher, unsere Kinder bis 13x = tunneln zwangsläufig). 0.8x Radius hält sicheren
    // Abstand zur Schwelle. Pod (worldR 0.20) darf damit doppelt so schnell sinken wie ein Kind
    // (worldR 0.11) - eine feste Zahl für beide wäre für den Pod unnötig zäh gewesen.
    private static readonly Dictionary<int, float> maxVertPerTick = new Dictionary<int, float>();

    public static float GetMaxVertPerTick(int id)
    {
        return maxVertPerTick.TryGetValue(id, out float v) ? v : Patch_SeekerFallClamp.DefaultMaxVertPerTick;
    }

    [HarmonyPriority(Priority.First)]
    static void Postfix(EntityAlive __instance)
    {
        if (!IsSeeker(__instance)) return;

        int id = __instance.entityId;

        EnforceHardLifetime(__instance, id);

        if (fixedCapsule.Contains(id)) return;

        CharacterControllerAbstract cc = __instance.m_characterController;
        if (cc == null) return;   // noch nicht initialisiert -> nächster Frame

        fixedCapsule.Add(id);

        float r = cc.GetRadius();
        float h = cc.GetHeight();

        // Physics-Node-Zustand mitmessen: eine korrekt dimensionierte Kapsel nützt nichts,
        // wenn der Collider deaktiviert ist oder auf einem Layer sitzt, der nicht gegen
        // Terrain testet. lossyScale sagt, wie groß die Kapsel WIRKLICH in der Welt ist.
        Transform pt = __instance.PhysicsTransform;
        string nodeInfo = "node=NULL";
        float scale = 1f;
        if (pt != null)
        {
            CapsuleCollider cap = pt.GetComponent<CapsuleCollider>();
            Vector3 ls = pt.lossyScale;
            scale = Mathf.Abs(ls.x) > 0.0001f ? ls.x : 1f;
            // ROHWERTE der Prefab-Kapsel mitloggen. Bisher wurde nur der Zustand NACH
            // `Entity.AddCharacterController` protokolliert, und der ist nicht dasselbe:
            // AddCharacterController liest center/height/radius vom CapsuleCollider und
            // übergibt sie als `SetSize(center, height / physicsHeightScale, radius)` weiter.
            // Nur die Rohwerte zeigen, was im deployten Bundle wirklich drinsteht - relevant,
            // weil die Prefab-QUELLE inzwischen height=2.76 sagt, zur Laufzeit aber h=2.000
            // ankommt. Passt `capRaw` nicht zur Quelle, ist das Bundle schlicht veraltet.
            string capRaw = cap != null
                ? $"capRaw(h={cap.height:0.000} r={cap.radius:0.000} cY={cap.center.y:0.000} dir={cap.direction} en={cap.enabled})"
                : "capRaw=noCap";
            // ACHTUNG: `r`/`h` SIND bereits Weltmeter - der Motor skaliert nicht (siehe
            // Konstanten oben). `lossyScale` bleibt nur zur Kontrolle im Log stehen, es geht
            // in keine Rechnung mehr ein.
            nodeInfo = $"layer={pt.gameObject.layer} active={pt.gameObject.activeInHierarchy} " +
                       $"lossyScale=({ls.x:0.00},{ls.y:0.00},{ls.z:0.00}) {capRaw} " +
                       $"collR={r:0.000} collH={h:0.000} (=Weltmeter)";
        }

        // Kapsel reparieren: (a) Entartung h == 2r (siehe Kopfkommentar) und (b) das CENTER.
        //
        // (b) ist der wichtigere Fehler: 7DTD erwartet die Kapsel STEHEND AUF dem Fusspunkt,
        // nicht um ihn zentriert. IL-Beleg aus `Entity.AddCharacterController`: der Auto-Fallback
        // ohne Prefab-Collider setzt center.y=0.9 bei height=1.8 (= h/2), und für Entities mit
        // physicsCapsuleCollider gilt ebenfalls `center.y = height * 0.5`.
        // Unser Prefab liefert center=(0,0,0) -> die Kapsel ist um den Fusspunkt ZENTRIERT und
        // ragt zur Hälfte UNTER die Entity-Position, steckt also dauerhaft im Boden. Ergebnis:
        // instabiles Grounding (im Log nur 18% `onGround=True`, 82% "in der Luft") trotz
        // korrektem Layer, korrekter Maske und gedeckelter Fallgeschwindigkeit.
        // ===== KOLLISIONSRADIUS AUF EIN TUNNELSICHERES MASS ANHEBEN (2026-07-25) =====
        //
        // Gemessen (Log `..._02-10-21`): die Kinder fielen mit bis zu 1.408 Einheiten pro Tick
        // bei einem WELT-Radius von nur 0.110 - also dem 13-fachen des eigenen Radius. Wer pro
        // Tick ein Vielfaches seines Radius zurücklegt, springt über dünne Böden hinweg, bevor
        // die Kollision sie auflösen kann. Die Referenz aus unserer Messreihe: vanilla Zombie
        // 0.32x = sicher, ab ~1x fängt das Tunneln an. Auch HORIZONTAL war es zu knapp:
        // 2.7 m/s = 0.135/Tick gegen worldR 0.110 = 1.23x.
        //
        // Statt die Geschwindigkeit immer weiter zu deckeln (das hat als einseitiger Clamp schon
        // einmal zu fliegenden Drohnen geführt) heben wir den Collider auf ein Mass an, das zur
        // Geschwindigkeit passt. Bei worldR 0.20 gilt horizontal 0.135/0.20 = 0.68x - unter der
        // Schwelle - und das Vertikalbudget (worldR * 0.8) steigt von 0.088 auf 0.16, die
        // Drohnen sinken also doppelt so natürlich wie mit dem alten festen Clamp.
        //
        // WARUM ZUR LAUFZEIT UND NICHT IM PREFAB: Pod und Kind teilen sich EIN Prefab und
        // unterscheiden sich nur über `SizeScale` (0.20 / 0.11). Ein grösserer Prefab-Radius
        // skaliert beide im selben Verhältnis mit; hier können wir pro Klasse einen ZIEL-Radius
        // in Weltmetern vorgeben.
        //
        // KEIN Schweben als Nebenwirkung: die Kapselunterkante liegt bei center.y - h/2 = 0,
        // also weiterhin exakt auf dem Fusspunkt. Ein grösserer Radius macht die Drohne nur
        // BREITER, sie hält damit ein paar Zentimeter mehr Abstand zur Wand - bei einem
        // schwebenden Drohnenmodell optisch unauffällig bis passend.
        // ===== DIE KAPSEL KOMMT JETZT AUS DEM PREFAB - HIER STEHT NUR NOCH EIN NETZ =====
        //
        // ECHTE URSACHE (IL `Entity.SetCCScale(float scale)`, vollstaendig):
        //     PhysicsTransform.localScale = Vector3.one;
        //     center = cc.GetCenter() * scale;
        //     height = cc.GetHeight() * scale;
        //     if (height < 2.2f && height > 1.89f) { height = 1.89f; center.y = height * 0.5f; }
        //     float rf = Utils.FastMax(scale, 1f);
        //     cc.SetSize(center, height, cc.GetRadius() * rf);
        //
        // `Utils.FastMax(scale, 1f)` pinnt den Radiusfaktor auf minimal 1: Center und Hoehe
        // schrumpfen mit `SizeScale`, der RADIUS NICHT. Bei SizeScale 0.11 war der Radius damit
        // relativ zur Hoehe um Faktor 9 zu gross, und `ValidateData` klemmte die Hoehe anschliessend
        // auf radius*2 hoch -> entartete Kugel (Hemisphaerenmittelpunkte fallen zusammen, jeder
        // Sweep/ProbeGround wertlos). Nachgerechnet an den Laufzeitwerten des Kindes:
        //     center 1.38 * 0.11 = 0.152 (beobachtet 0.152)
        //     height 2.76 * 0.11 = 0.304 -> von ValidateData auf 2.000 geklemmt (beobachtet 2.000)
        //     radius 1.0 * FastMax(0.11,1) = 1.0 (beobachtet 1.000)
        // Alle drei Werte reproduzieren die Formel exakt. Das war die gemeinsame Ursache von
        // Durchfallen, Kreisen, Wandklettern und Bouncen - und sie ist zur Laufzeit nicht sauber
        // reparierbar, weil sie VOR unserem Postfix passiert.
        //
        // KORREKTUR AN DER QUELLE (2026-07-25 c): Prefab-Kapsel jetzt in echten Metern
        // (r=0.17 h=0.40 cY=0.20) und `SizeScale` nie mehr unter 1 - Kind 1.0, Pod 1.818. Bei
        // scale >= 1 ist FastMax ein No-Op, also skalieren Radius, Hoehe und Center konsistent.
        //
        // HISTORIE (falsche Faehrten, damit sie niemand zurueckholt): `physicsHeightScale` ist
        // NICHT 1/SizeScale, sondern eine Konstante (1.09 bzw. 1.0) - eine Kapselkorrektur daraus
        // abzuleiten war Zufallstreffer-Arithmetik. Und eigene Werte fuer Hoehe/Center zu setzen
        // (Build 12:13: h=0.50 cY=0.25) hat den Bodenkontakt komplett zerstoert: onGround in 0 von
        // 52 Zeilen, Pods stiegen in Phase ARMED - bei reinem `moveHelper.Stop()`, also ohne jede
        // Eigenbewegung - um 2.21 bis 3.26 m auf.
        //
        // DESHALB GREIFT DER CODE HIER NUR NOCH IM DEFEKTFALL. Ist die Kapsel gesund, bleibt sie
        // unangetastet - sonst koennten wir am Log nie ablesen, ob das Prefab wirklich stimmt.
        bool isPod = __instance.EntityClass == podClass;

        Vector3 oldCenter = cc.GetCenter();

        // Gesundheitskriterien, beide direkt aus der Geometrie der Kapsel:
        //  (a) radius < height/2 - sonst ist sie zur Kugel entartet (das eigentliche Symptom).
        //  (b) center.y ~ height/2 - die Kapsel muss AUF dem Fusspunkt stehen, nicht um ihn herum.
        bool degenerate = r >= h * 0.5f - 0.001f;
        bool centerOff = Mathf.Abs(oldCenter.y - h * 0.5f) > 0.02f;
        bool healthy = !degenerate && !centerOff && h > 0.05f;

        if (healthy)
        {
            // Regelfall nach der Prefab-Korrektur: nichts tun, nur protokollieren.
            capsuleInfo[id] = $"capsulePREFAB-OK h={h:0.000} r={r:0.000} centerY={oldCenter.y:0.000} " +
                              $"[r/hHalf={(r / (h * 0.5f)):0.00}] | {nodeInfo}";
        }
        else
        {
            // Netz: greift nur, wenn das Prefab (noch) nicht stimmt - z.B. wenn ein Bundle-Export
            // vergessen wurde. Hoehe aus der vorhandenen Geometrie ableiten und den Radius unter
            // h/2 druecken, damit ValidateData nicht erneut hochklemmt.
            float wantH = Mathf.Max(h, 0.05f);
            float wantCenterY = wantH * 0.5f;
            float maxR = wantH * 0.5f - 0.005f;
            float wantR = Mathf.Min(isPod ? PodCapsuleRadius : ChildCapsuleRadius, maxR);

            cc.SetSize(new Vector3(0f, wantCenterY, 0f), wantH, wantR);

            // `hSet` MUSS gleich `wantH` sein. Weicht es ab, hat ValidateData die Hoehe wieder auf
            // 2*r hochgezogen - dann ist der Radius immer noch zu gross.
            Debug.LogWarning($"[SeekerPhysDbg] {id} capsule war DEFEKT (degenerate={degenerate} " +
                             $"centerOff={centerOff}) - Prefab/Bundle pruefen!");
            capsuleInfo[id] = $"capsuleRESCUED h={h:0.000}->{cc.GetHeight():0.000} " +
                              $"r={r:0.000}->{cc.GetRadius():0.000} " +
                              $"centerY={oldCenter.y:0.000}->{wantCenterY:0.000} " +
                              $"[wantH={wantH:0.000} hSet={cc.GetHeight():0.000} rCap={maxR:0.000}] | {nodeInfo}";
        }

        // Vertikalbudget aus dem Radius. Der Radius IST der Weltwert (der Motor rechnet nicht um,
        // und `SetCCScale` hat ihn bei SizeScale >= 1 sauber mitskaliert), also OHNE lossyScale.
        maxVertPerTick[id] = Mathf.Max(0.04f, cc.GetRadius() * 0.8f);

        // Stufenhöhe an die tatsächliche Kapselhöhe koppeln.
        // `AddCharacterController` schliesst mit `SetStepOffset(Entity.stepHeight)`, und
        // `stepHeight` ist ein WELT-Wert, den wir vom Rabbit erben (rund 20x so gross wie unsere
        // Drohne). Eine 0.4m hohe Drohne, die 0.5m hohe Stufen nimmt, klettert effektiv an allem
        // hoch - das ist der "die klettern Wände hoch"-Anteil, der nichts mit der Fallgeschwindigkeit
        // zu tun hatte. Ein Drittel der eigenen Höhe ist ein plausibles Mass.
        // Bewusst aus `cc.GetHeight()` gelesen statt aus einer eigenen Konstante: so stimmt der Wert
        // in beiden Zweigen oben (Prefab-OK wie Netz) und bleibt bei kuenftigen Prefab-Aenderungen
        // automatisch richtig.
        cc.SetStepOffset(cc.GetHeight() * 0.33f);
        capsuleInfo[id] += $" maxVert={maxVertPerTick[id]:0.000}";

        // HISTORIE Layer/Kollisionsmaske (Runtime-Patch hier ENTFERNT, 2026-07-23):
        // Lange Zeit lag die Physics-Node auf Layer 0 (Default) statt auf 15 ("CC Physics").
        // `KinematicCharacterMotor.Awake()` baut seine Kollisionsmaske EINMALIG aus dem Layer des
        // eigenen GameObjects (IL-verifiziert):
        //     CollidableLayers = 0;
        //     for (i = 0..31) if (!Physics.GetIgnoreLayerCollision(gameObject.layer, i))
        //                         CollidableLayers |= 1 << i;
        // Mit der Default-Maske testete der Motor weder gegen Terrain noch gegen Bloecke ->
        // `collisionFlags` immer `None`, `IsGrounded()` immer false, Drohnen fielen durch alles.
        // Ein spaeteres Setzen des Layers allein reicht NICHT, weil Awake() laengst gelaufen ist;
        // die Maske musste explizit neu berechnet werden.
        // Das Prefab liefert die Node inzwischen selbst auf Layer 15 (im Log 44/44 Entities:
        // "PREFAB-OK layer=15 mask=0xE6C181C0"), der Runtime-Patch war also nur noch ein No-Op.
        // Falls ein kuenftiger Prefab-Export das wieder verliert, ist das Symptom eindeutig:
        // collFlags dauerhaft None -> dann diesen Block aus der Git-Historie zurueckholen.
        if (pt != null)
            capsuleInfo[id] += $" | layer={pt.gameObject.layer}";

        Debug.Log($"[SeekerPhysDbg] {id} {capsuleInfo[id]}");
    }
}

// Zählt die tatsächlichen Aufrufe des Kollisionspfads UND loggt den Vektor, der wirklich an
// die Kollisionsauflösung übergeben wird.
//
// WARUM der Parameter und nicht `Entity.motion`: `ccEntityCollisionResults` SCHREIBT
// `Entity.motion` nachträglich um (projectedMove-Skalierung), das geloggte Feld ist also nicht
// zwingend das, was der CharacterController bekommen hat. Im letzten Log stand
// `motion=(0.00,-0.08,0.00)` -- horizontal null -- obwohl sich die Drohne sichtbar horizontal
// bewegte. Diese Diskrepanz ist der letzte ungeklärte Punkt: bekommt der Controller gar keine
// Horizontalbewegung (dann wird die Position anderswo direkt gesetzt und umgeht die Kollision),
// oder bekommt er sie und löst sie nur nicht auf?
// TUNNELING-FIX: begrenzt, wie weit eine Drohne pro Physik-Tick fallen darf.
//
// Messung (Log 2026-07-23 13:53) -- Fallweg pro Tick im Verhältnis zum eigenen Kollisionsradius:
//   vanilla Zombie:  0.097m Fall / 0.299m Radius = 0.32x  -> unkritisch
//   Seeker-Kind:     0.489m Fall / 0.110m Radius = 4.4x   -> tunnelt zwangsläufig
// Die Drohnen sind durch SizeScale (0.11 bzw. 0.20) winzig, fallen aber mit voller Gravitation.
// Wer pro Tick ein Vielfaches seines eigenen Durchmessers zurücklegt, springt über dünne
// Böden/Wände hinweg, bevor eine Kollision aufgelöst werden kann -- daher das Durchfallen und
// das anschliessende endlose Fallen UNTER dem Terrain (beobachtet: y~34-36 bei Boden 38).
//
// Die Kapsel einfach zu vergrössern ist keine gute Option: sie ist mit worldR=0.11 korrekt an
// die sichtbare Drohne angepasst; ein grösserer Radius liesse sie sichtbar über dem Boden
// schweben. Stattdessen deckeln wir die Fallgeschwindigkeit auf weniger als einen Radius pro
// Tick. 0.08m/Tick = 1.6 m/s -- für eine schwebende Drohne optisch völlig plausibel.
// Angesetzt wird direkt am Vektor, der in die Kollisionsauflösung geht (nicht am
// `Entity.motion`-Feld, das nachträglich überschrieben wird).
[HarmonyPatch(typeof(Entity), "entityCollision")]
// NACHTRAG 2026-07-25 (Log `..._01-09-50`): DIESER CLAMP WIRD UMGANGEN.
// Messung: Kind 21818 fiel von y≈39.5 auf y≈0.90 in ~2s (Zeitstempel der umgebenden
// Engine-Zeilen) = ~19 m/s. Bei wirksamen 0.08/Tick = 1.6 m/s hätte derselbe Sturz ~24s
// gedauert - und das Kind wäre längst durch seine 10s-lifetime entfernt worden. 24 von 90
// Kindern erreichten so das Grundgestein.
// Der Prefix hängt an `Entity.entityCollision`; wird dieser Pfad in der Sturzphase nicht
// durchlaufen, greift der Clamp nie. Statt weiter zu raten:
//   (a) ZWEITE Absicherung direkt am `motion`-Feld (Patch_SeekerMotionClamp unten), die
//       unabhängig vom Kollisionspfad wirkt, und
//   (b) ein ZÄHLER pro Entity, den die Rettungsmeldung mit ausgibt. Steht dort beim nächsten
//       Sturz `clampHits=0`, ist bewiesen, dass `entityCollision` in der Sturzphase gar nicht
//       läuft; ein hoher Wert hiesse, der Clamp greift und etwas ÜBERSCHREIBT die Bewegung
//       danach. Eine der beiden Antworten schliesst die Ursache endgültig ein.
public static class Patch_SeekerFallClamp
{
    // Fallback, solange die Kapselmasse einer Entity noch nicht vermessen ist.
    public const float DefaultMaxVertPerTick = 0.08f;

    private static readonly Dictionary<int, int> clampHits = new Dictionary<int, int>();

    public static int GetClampHits(int id)
    {
        return clampHits.TryGetValue(id, out int n) ? n : 0;
    }

    public static void ForgetEntity(int id)
    {
        clampHits.Remove(id);
    }

    static void Prefix(Entity __instance, ref Vector3 _motion)
    {
        if (!(__instance is EntityAlive alive) || !Patch_SeekerPhysics.IsSeeker(alive)) return;

        int id = alive.entityId;
        float max = Patch_SeekerPhysics.GetMaxVertPerTick(id);
        if (_motion.y >= -max && _motion.y <= max) return;

        _motion.y = Mathf.Clamp(_motion.y, -max, max);
        clampHits.TryGetValue(id, out int n);
        clampHits[id] = n + 1;
    }
}

// WIEDER EINGEFÜHRT 2026-07-25 - diesmal SYMMETRISCH.
//
// Dieser Patch war schon einmal da und wurde entfernt, weil die Drohnen mit ihm abhoben und
// Wände hochkletterten. Der Defekt war NICHT die Idee, sondern die Einseitigkeit: er deckelte
// nur `motion.y < -x` und liess Aufwärtsbewegung unbegrenzt. Damit war die Schwerkraft faktisch
// abgeschaltet - jeder Aufwärtsimpuls (Kollisionsauflösung, die die Kapsel aus einer Wand
// herausdrückt; Stufen; Hänge) blieb stehen, während das Zurücksinken in Zeitlupe lief.
//
// Warum er trotzdem gebraucht wird (Messung Log `..._02-10-21`): der Prefix am echten
// Kollisionsvektor feuert inzwischen zuverlässig (`clampHits` 16-72 statt 0-8), TROTZDEM stand
// `Entity.motion.y` beim Sturz bei -0.443 bis -1.408 pro Tick = 8.8 bis 28 m/s. Die beiden
// Grössen sind entkoppelt: `ccEntityCollisionResults` schreibt das `motion`-FELD nach der
// Kollisionsauflösung neu, der geklemmte Parameter wirkt also nicht auf die Beschleunigung des
// nächsten Ticks. Bei worldR 0.11 sind 0.5/Tick das 4- bis 13-fache des eigenen Radius; die
// Tunnelschwelle liegt nach unserer Messreihe bei ~1x (vanilla Zombie 0.32x). Genau deshalb
// fallen die Kinder weiterhin durch Böden, obwohl der Clamp "greift".
//
// Symmetrisch begrenzt löst beides zugleich: nach unten keine Tunnelgeschwindigkeit mehr, nach
// oben kein Aufschaukeln. Die Grenze skaliert pro Entity mit dem echten Collider-Radius
// (Patch_SeekerPhysics.GetMaxVertPerTick), statt für Pod und Kind dieselbe Zahl zu nehmen.
// Nebeneffekt, bewusst in Kauf genommen: die Drohnen sinken gemächlich statt zu stürzen - für
// schwebende SciFi-Pods thematisch stimmig, und deutlich besser als durch den Boden zu fallen.
[HarmonyPatch(typeof(EntityAlive), "OnUpdateEntity")]
public static class Patch_SeekerMotionClamp
{
    [HarmonyPriority(Priority.First)]
    static void Postfix(EntityAlive __instance)
    {
        if (!Patch_SeekerPhysics.IsSeeker(__instance)) return;

        float max = Patch_SeekerPhysics.GetMaxVertPerTick(__instance.entityId);
        Vector3 m = __instance.motion;
        if (m.y < -max || m.y > max)
        {
            m.y = Mathf.Clamp(m.y, -max, max);
            __instance.motion = m;
        }
    }
}

// ENTFERNT 2026-07-25: `Patch_SeekerMotionClamp` (Postfix auf EntityAlive.OnUpdateEntity, der
// `Entity.motion.y` jeden Frame auf >= -0.08 deckelte).
//
// WARUM ER WEG MUSS - er hat ein neues, schlimmeres Symptom erzeugt und das alte nicht gelöst:
//  * Die Deckelung war EINSEITIG: nach unten 1.6 m/s, nach oben unbegrenzt. Damit war die
//    Schwerkraft praktisch abgeschaltet. Jeder Aufwärtsimpuls (Kollisionsauflösung, die die
//    Kapsel aus einer Wand herausdrückt, Stufen, Hänge) blieb stehen, statt weggedämpft zu
//    werden - die Drohnen stiegen und sanken nur noch mit Zeitlupentempo zurück. Das ist exakt
//    das gemeldete "heben ab, klettern Wände hoch, fliegen ein wenig".
//    Log-Beleg: 11 von 61 Rettungen holten eine Drohne von OBERHALB ihres letzten
//    Bodenkontakts herunter (z.B. `y=44.76 -> 37.65`, also 7 m über dem Boden).
//  * Gebracht hat er nichts: die Stürze blieben (68 Rettungen statt 26).
// Die Fallgeschwindigkeit ist damit wieder vanilla. Gegen das Durchfallen schützt jetzt allein
// das Anker-Rettungsnetz in Patch_SeekerRoll, das im selben Log nachweislich früh greift
// (Rettung #1 durchweg bei y≈33-35 gegen Anker ≈37.5, also nach ~3-4 m).

// Zentrales Aufräumen aller per-Entity-Zustände beim Entladen einer Entity.
//
// `Entity.OnEntityUnload()` ist public+virtual und läuft für JEDE Entity, die die Welt
// verlässt - auch für die, die wir per MarkToUnload selbst entfernen. Das ist der einzige
// Punkt, an dem wir zuverlässig erfahren, dass eine entityId frei wird.
// Bewusst OHNE IsSeeker-Filter: `Dictionary.Remove` auf einen fehlenden Schlüssel ist ein
// billiger No-Op, und so verpassen wir auch die Fälle, in denen die EntityClass-Auflösung
// gerade nicht greift.
[HarmonyPatch(typeof(Entity), "OnEntityUnload")]
public static class Patch_SeekerCleanup
{
    static void Postfix(Entity __instance)
    {
        if (__instance == null) return;
        int id = __instance.entityId;
        Patch_SeekerPhysics.ForgetEntity(id);
        Patch_SeekerRoll.ForgetEntity(id);
        Patch_SeekerFallClamp.ForgetEntity(id);
        Patch_SeekerStepLimiter.ForgetEntity(id);
        SeekerMove.Forget(id);
    }
}

// Zaehlt die Aufrufe des Kollisionspfads pro Entity. Der Zaehler unterscheidet
// "Kollision laeuft, findet aber nichts" von "Kollisionspfad wird gar nicht erst betreten"
// (dann kaeme die Bewegung von woanders und umginge die Physik) und wird von der
// Pod-Diagnose mit ausgegeben. Der frueher hier haengende [SeekerMoveDbg]-Sekundenlog ist
// entfernt: die Physik ist vermessen, er hat nur noch das Log geflutet.
[HarmonyPatch(typeof(Entity), "entityCollision")]
public static class Patch_SeekerCollisionCounter
{
    static void Postfix(Entity __instance)
    {
        if (!(__instance is EntityAlive alive) || !Patch_SeekerPhysics.IsSeeker(alive)) return;
        int id = alive.entityId;
        Patch_SeekerPhysics.CollisionCalls.TryGetValue(id, out int n);
        Patch_SeekerPhysics.CollisionCalls[id] = n + 1;
    }
}
