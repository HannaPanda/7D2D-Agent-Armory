using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

// Makes the seeker drones ROLL like a ball as they move (Division-style).
//
// We rotate the INNERMOST mesh transform (the actual Renderer's transform), NOT the
// model root: the entity's yaw/facing is applied to the model root by the engine, so
// rolling there would fight it. The mesh child is never touched by the game, so our
// accumulated world-space roll persists cleanly. Since the drone is a symmetric ball,
// the parent yaw is visually irrelevant.
//
// Roll angle per tick = distanceMoved / radius (radians), about the axis perpendicular
// to the horizontal movement direction (Cross(up, moveDir)).
[HarmonyPatch(typeof(EntityAlive), "OnUpdateEntity")]
public static class Patch_SeekerRoll
{
    private static EntityClass podClass, childClass;
    private static bool resolved;

    // Per-entity state (keyed by entityId).
    private static readonly Dictionary<int, Vector3>   lastPos = new Dictionary<int, Vector3>();
    private static readonly Dictionary<int, Transform> meshTf  = new Dictionary<int, Transform>();
    private static readonly Dictionary<int, float>     radius  = new Dictionary<int, float>();
    // Model-Root + vertikaler Korrekturversatz (siehe ApplyModelOffset).
    private static readonly Dictionary<int, Transform> modelTf    = new Dictionary<int, Transform>();
    private static readonly Dictionary<int, float>     modelYFix  = new Dictionary<int, float>();

    // EXPLIZITE REIHENFOLGE: Patch_SeekerPhysics postfixt dieselbe Methode und repariert dort
    // die Kollisionskapsel (Höhe + Center). Dieser Patch liest anschliessend `onGround` und die
    // Renderer-Bounds und setzt den Modell-Offset - er muss also NACH der Kapselreparatur
    // laufen, sonst entscheidet im ersten Frame die (undefinierte) Harmony-Reihenfolge.
    // Höhere Priority läuft zuerst: Physics = First, Roll = Last.
    [HarmonyPriority(Priority.Last)]
    static void Postfix(EntityAlive __instance)
    {
        if (__instance == null) return;

        // Resolve our two classes once; compare by reference (EntityClass.list holds singletons).
        if (!resolved)
        {
            podClass   = EntityClass.GetEntityClass(EntityClass.FromString("entitySeekerCluster"));
            childClass = EntityClass.GetEntityClass(EntityClass.FromString("entitySeekerChild"));
            resolved = true;
        }
        EntityClass ec = __instance.EntityClass;
        if (ec == null || (ec != podClass && ec != childClass)) return;

        int id = __instance.entityId;

        // Cache the mesh transform + rolling radius the first time we see this entity.
        if (!meshTf.TryGetValue(id, out Transform mesh) || mesh == null)
        {
            Transform model = __instance.emodel != null ? __instance.emodel.GetModelTransform() : null;
            if (model == null) return;
            Renderer r = model.GetComponentInChildren<Renderer>();
            if (r == null) return;
            mesh = r.transform;
            meshTf[id] = mesh;
            Vector3 ext = r.bounds.extents;              // world-space half-size ≈ ball radius
            float rad = (ext.x + ext.z) * 0.5f;
            radius[id] = rad > 0.05f ? rad : 0.3f;

            // Blue glow so the drones are visible at night. Point light on a child of the
            // model root (position only — rotation/roll doesn't matter for a point light).
            // EXPERIMENTAL: if the game's renderer doesn't pick up a runtime-added Unity Light,
            // we'll move this into the Unity prefab instead.
            Transform model2 = __instance.emodel != null ? __instance.emodel.GetModelTransform() : null;
            if (model2 != null)
            {
                GameObject lightGo = new GameObject("SeekerLight");
                lightGo.transform.SetParent(model2, false);
                lightGo.transform.localPosition = Vector3.zero;
                Light li = lightGo.AddComponent<Light>();
                li.type = LightType.Point;
                li.color = new Color(0.25f, 0.55f, 1f);  // cyan-blue
                li.range = 6f;
                li.intensity = 2.5f;
                li.bounceIntensity = 0f;
                li.shadows = LightShadows.None;
            }
        }

        // Mesh vertikal auf den Fusspunkt setzen.
        //
        // Die Kollisionskapsel steht seit dem Center-Fix (Patch_SeekerPhysics: center.y = h/2,
        // wie vanilla) korrekt AUF der Entity-Position: ihre Unterkante liegt bei y=0 relativ
        // zur Entity. Das Drohnen-Mesh ist im Prefab aber um seinen Mittelpunkt zentriert, ragt
        // also um seinen Radius UNTER die Entity-Position -> die Kugel steckt sichtbar halb im
        // Boden. (Vorher passte es optisch nur zufällig, weil auch die Kapsel zentriert war --
        // um den Preis, dass die Physik nicht funktionierte.)
        // Korrektur = Abstand von der Mesh-Unterkante zur Entity-Position, aus den echten
        // Renderer-Bounds berechnet statt geraten, und in lokale Einheiten des Model-Parents
        // umgerechnet (die Entity ist per SizeScale skaliert).
        ApplyModelOffset(__instance, id);

        Vector3 cur = __instance.position;

        cur = RescueIfLost(__instance, id, cur);

        if (lastPos.TryGetValue(id, out Vector3 prev))
        {
            Vector3 d = cur - prev; d.y = 0f;            // horizontal travel only
            float dist = d.magnitude;
            if (dist > 0.0005f && dist < 8f)             // ignore teleport/snap jumps
            {
                Vector3 axis = Vector3.Cross(Vector3.up, d / dist);
                float deg = dist / radius[id] * Mathf.Rad2Deg;
                mesh.Rotate(axis, deg, Space.World);
            }
        }
        lastPos[id] = cur;

        // Notbremse gegen Wachstum. Der reguläre Weg ist ForgetEntity() aus
        // Patch_SeekerCleanup; das hier greift nur, falls ein Unload einmal nicht durchkommt.
        if (lastPos.Count > 4000)
        {
            lastPos.Clear(); meshTf.Clear(); radius.Clear();
            modelTf.Clear(); modelYFix.Clear();
            lastY.Clear();
            lastGrounded.Clear(); rescueCount.Clear();
        }
    }

    // ================= Rettungsnetz "durch die Welt gefallen" =================
    //
    // ALTE FASSUNG WAR FALSCH (Ursache für "landet nicht richtig" + Wand-Clipping, 2026-07-25):
    // sie verglich `entity.position.y` gegen `World.GetHeightAt(x, z)` und teleportierte bei
    // einer Differenz von nur 0.8m per `SetPosition(..., true)` nach oben - und zwar in JEDEM
    // Frame, in dem die Bedingung galt.
    //
    // `World.GetHeightAt` ist laut IL exakt
    //     ChunkCache.ChunkProvider.GetTerrainGenerator().GetTerrainHeightAt((int)x, (int)z)
    // also die Höhe des ROHGELÄNDES. Sie kennt weder POI-Böden noch Keller, Minenschächte,
    // Brücken, eingeschnittene Straßen noch irgendeinen gesetzten Block, und sie schneidet x/z
    // auf int ab (keine Interpolation - an einem Hang liegt die abgetastete Säule leicht mehr
    // als 0.8m daneben). Jede Drohne, die legitim unter Geländeniveau stand, wurde damit
    // dauerhaft an die Oberfläche gerissen - quer durch das Gebäude, in dem sie stand.
    //
    // NEUE LOGIK: keine Höhenkarte als Kriterium. Auch die blockbewusste `World.GetHeight`
    // (IChunk.GetHeight) taugt dafür nicht - sie liefert die OBERSTE feste Blockhöhe, also bei
    // einem dreistöckigen POI das Dach; eine Drohne im Erdgeschoss wäre danach ebenfalls
    // "10m zu tief". Stattdessen zwei Kriterien, die beide POI-sicher sind:
    //   (1) HARTE UNTERGRENZE: unterhalb von y=2 ist nichts mehr legitim.
    //   (2) FREIFALL-WATCHDOG: nicht am Boden UND fallend, ununterbrochen länger als
    //       RescueSeconds. Wer wirklich durch die Welt gerutscht ist, fällt dauerhaft; wer nur
    //       im Keller steht, ist `onGround` und löst nie aus.
    // Erst wenn eines davon zutrifft, wird - dann mit der blockbewussten `GetHeight` als Ziel -
    // zurückgesetzt.
    // ZWEITE KORREKTUR (2026-07-25, nach Log `..._01-09-50`): die erste Fassung dieses Netzes
    // hatte selbst zwei Fehler, beide im Log sichtbar.
    //
    // (1) DAS ZIEL WAR FALSCH. Die Rettung setzte auf `GetHeight(x,z) + 0.5`, also die OBERSTE
    //     feste Blockhöhe. Bei einem POI ist das das DACH: im Log landeten Drohnen auf
    //     `y=48.50` und `y=47.50`, während Nachbarn im selben Gebiet auf `37.50` kamen. Eine
    //     Drohne aufs Dach zu teleportieren ist keine Rettung.
    //     JETZT: gemerkte LETZTE Position, an der die Drohne nachweislich Bodenkontakt hatte.
    //     Die ist per Definition ein gültiger Standplatz - unabhängig von Stockwerk, Keller
    //     oder Höhenkarte. Nur wenn es die noch nicht gibt (Sturz vor dem ersten Bodenkontakt),
    //     fällt es auf die Höhenkarte zurück.
    //
    // (2) DIE SCHWELLE WAR ZU TRÄGE. Der Freifall-Watchdog stand auf 4s. Gemessen (Zeitstempel
    //     der umgebenden Engine-Zeilen): Kind 21818 fiel von y≈39.5 auf y≈0.90 in etwa ZWEI
    //     Sekunden - also ~19 m/s, freier Fall. Der Watchdog kam nie zum Zug; alle 26 Rettungen
    //     im Log liefen über die harte Untergrenze y<2, d.h. die Drohne war schon fast am
    //     Grundgestein. Genau diese zwei Sekunden sieht der Spieler als "die landen nicht /
    //     fallen durch alles".
    //     JETZT: 0.8s Freifall ODER mehr als 3m unter dem letzten Bodenkontakt.
    //
    // Bewusst NICHT geändert: das Kriterium bleibt POI-sicher, weil es nie eine Höhenkarte
    // gegen die aktuelle Höhe stellt (siehe Kommentar oben).
    // DRITTE KORREKTUR (2026-07-25, Log `..._01-27-47`): der reine Freifall-Timer ist RAUS.
    // Er war ein Falsch-Positiv-Generator: eine Drohne, die aus der Höhe wieder herunterkommt,
    // sinkt genauso "ununterbrochen" wie eine durchgefallene - und wurde deshalb mitten im
    // Sinkflug an den Boden getackert. Im Log holten 11 von 61 Rettungen eine Drohne von
    // OBERHALB ihres letzten Bodenkontakts herunter (bis `y=44.76 -> 37.65`).
    // Der Anker-Test deckt den echten Fall vollständig ab und kann per Konstruktion nicht
    // fälschlich auslösen: unter dem letzten bestätigten Standplatz zu sein ist etwas, das eine
    // korrekt laufende Drohne nie ist. Übrig bleiben zwei Kriterien - Anker und harte
    // Untergrenze -, beide POI-sicher.
    private const float RescueDropBelowGround = 3f;
    private const float RescueMinY = 2f;
    private const int MaxRescues = 3;

    private static readonly Dictionary<int, float> lastY = new Dictionary<int, float>();
    private static readonly Dictionary<int, Vector3> lastGrounded = new Dictionary<int, Vector3>();
    private static readonly Dictionary<int, int> rescueCount = new Dictionary<int, int>();

    private static Vector3 RescueIfLost(EntityAlive e, int id, Vector3 cur)
    {
        World w = e.world;
        if (w == null) return cur;

        // Letzten gültigen Standplatz merken - das ist der Anker jeder Rettung.
        if (e.onGround) lastGrounded[id] = cur;

        bool hasAnchor = lastGrounded.TryGetValue(id, out Vector3 anchor);
        bool lost = cur.y < RescueMinY
                 || (hasAnchor && !e.onGround && cur.y < anchor.y - RescueDropBelowGround);
        if (!lost) return cur;

        // Wiederholtes Yo-Yo ist schlimmer als ein sauberes Verschwinden: wer dreimal gerettet
        // werden musste, steckt in einer Geometrie, aus der er nicht herauskommt.
        rescueCount.TryGetValue(id, out int n);
        n++;
        rescueCount[id] = n;
        if (n > MaxRescues)
        {
            Debug.LogWarning($"[SeekerPhysDbg] {id} nach {MaxRescues} Rettungen erneut gefallen - entferne Entity");
            if (!e.IsMarkedForUnload()) e.MarkToUnload();
            return cur;
        }

        Vector3 rescued;
        string via;
        if (hasAnchor)
        {
            rescued = anchor;
            via = "lastGrounded";
        }
        else
        {
            // Notfall: noch nie Bodenkontakt gehabt. GetHeight(int,int) liest die
            // Chunk-Höhenkarte (IChunk.GetHeight, inkl. POI-/Spielerblöcken) - als Notanker
            // besser als nichts, kann aber ein Dach treffen (siehe (1) oben).
            float ground = w.GetHeight(Utils.Fastfloor(cur.x), Utils.Fastfloor(cur.z));
            rescued = new Vector3(cur.x, ground + 0.5f, cur.z);
            via = "heightmap";
        }

        // Dem Weg-pro-Tick-Begrenzer sagen, dass DIESER Sprung gewollt ist - sonst zieht er den
        // Rettungsteleport im selben Tick wieder auf Schrittweite zurueck und das Netz wirkt nicht.
        Patch_SeekerStepLimiter.AllowTeleport(id);
        e.SetPosition(rescued, true);

        // Restbewegung nach unten löschen, sonst rauscht die Drohne im nächsten Tick mit
        // unverändertem Impuls sofort wieder durch den Boden.
        // (Der Messwert wird VOR der Korrektur gelesen - die erste Fassung loggte den bereits
        // genullten Wert und zeigte deshalb immer `motionY=0.000`, also nichts.)
        Vector3 m = e.motion;
        float motionYBefore = m.y;
        if (m.y < 0f) { m.y = 0f; e.motion = m; }

        lastY[id] = rescued.y;
        Debug.LogWarning($"[SeekerPhysDbg] {id} RESCUE #{n} via {via}: y={cur.y:0.00} -> {rescued.y:0.00} " +
                         $"| anchorY={(hasAnchor ? anchor.y : -1f):0.00} " +
                         $"clampHits={Patch_SeekerFallClamp.GetClampHits(id)} onGround={e.onGround} " +
                         $"motionY={motionYBefore:0.000}");
        return rescued;
    }

    // Aufgeräumt aus Patch_SeekerCleanup (Postfix auf Entity.OnEntityUnload).
    public static void ForgetEntity(int id)
    {
        lastPos.Remove(id); meshTf.Remove(id); radius.Remove(id);
        modelTf.Remove(id); modelYFix.Remove(id);
        lastY.Remove(id);
        lastGrounded.Remove(id); rescueCount.Remove(id);
    }

    // Hebt den Model-Root so an, dass die Mesh-Unterkante auf der Entity-Position (Fusspunkt)
    // sitzt. Der Versatz wird einmal pro Entity aus den Renderer-Bounds bestimmt und danach
    // jeden Frame gesetzt, weil der Model-Root von der Engine neu positioniert werden kann.
    // Bewusst am Model-Root und NICHT am Mesh-Child: dessen Transform trägt die Roll-Rotation.
    private static void ApplyModelOffset(EntityAlive e, int id)
    {
        if (!modelTf.TryGetValue(id, out Transform model) || model == null)
        {
            model = e.emodel != null ? e.emodel.GetModelTransform() : null;
            if (model == null) return;
            Renderer r = model.GetComponentInChildren<Renderer>();
            if (r == null) return;

            // Abstand von der Mesh-Unterkante hoch zur Entity-Basis.
            //
            // WICHTIG: hier MUSS `e.transform.position` stehen, nicht `e.position`. 7DTD
            // verschiebt den Weltursprung (Origin-Shifting): `ccEntityCollisionResults` rechnet
            // `Entity.position = PhysicsTransform.position + Origin.position`. `Entity.position`
            // liegt also in verschobenen "Welt"-Koordinaten, `Renderer.bounds` dagegen in reinen
            // Unity-Koordinaten. Beide zu mischen schlug den kompletten Origin-Versatz (mehrere
            // hundert Meter) auf den Offset auf -> das Mesh flog aus dem Sichtbereich und die
            // Drohne wurde unsichtbar. `e.transform.position` ist dasselbe System wie bounds.
            float deltaWorld = e.transform.position.y - r.bounds.min.y;
            // ... in lokale Einheiten des Parents umrechnen (SizeScale steckt in lossyScale).
            Transform parent = model.parent;
            float scaleY = parent != null ? parent.lossyScale.y : 1f;
            if (Mathf.Abs(scaleY) < 0.0001f) scaleY = 1f;

            float deltaLocal = deltaWorld / scaleY;

            // Sicherung: der Versatz kann nur in der Größenordnung der Drohne liegen. Ein
            // absurder Wert bedeutet, dass die Bounds noch nicht initialisiert sind oder wieder
            // Koordinatensysteme durcheinandergeraten -- dann lieber gar nicht verschieben als
            // das Modell erneut aus der Welt zu schießen.
            if (!(Mathf.Abs(deltaLocal) < 5f))
            {
                Debug.LogWarning($"[SeekerPhysDbg] {id} model offset verworfen: deltaLocal={deltaLocal:0.000} " +
                                 $"(deltaWorld={deltaWorld:0.000} scaleY={scaleY:0.000})");
                return;
            }

            modelTf[id] = model;
            modelYFix[id] = model.localPosition.y + deltaLocal;
            Debug.Log($"[SeekerPhysDbg] {id} modelOffset localY {model.localPosition.y:0.000}" +
                      $" -> {modelYFix[id]:0.000} (deltaLocal={deltaLocal:0.000})");
        }

        if (modelYFix.TryGetValue(id, out float wantY))
        {
            Vector3 lp = model.localPosition;
            if (Mathf.Abs(lp.y - wantY) > 0.0005f)
            {
                lp.y = wantY;
                model.localPosition = lp;
            }
        }
    }
}
