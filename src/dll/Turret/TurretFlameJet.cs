using System.Collections.Generic;
using UnityEngine;

// EINE Flamme am Rohr, solange geschossen wird - statt einer pro Schuss.
//
// ⚠ WARUM DAS UEBERHAUPT NOETIG IST. Vanilla spielt den Muendungseffekt so ab
// (`MiniTurretFireController.Fire`, IL_0503..IL_055C):
//
//     if (!IsNullOrEmpty(muzzleFireParticle))
//         FireControllerUtils.SpawnParticleEffect(
//             new ParticleEffect(muzzleFireParticle, Muzzle.position + Origin.position,
//                                Muzzle.rotation, 1f, Color.white, fireSound, null, 1f, ""), -1);
//
// Drei Eigenschaften dieses Aufrufs sind fuer einen kurzen Mündungsblitz richtig und fuer eine
// Flamme falsch:
//
//   (1) Die `-1` als verursachende Entity schaltet in `ParticleEffect.SpawnParticleEffect` den
//       Recycling-Pfad ab (IL_006F: `if (_entityThatCausedIt == -1) goto instantiate`). Mit einer
//       echten Id haelt vanilla pro Entity eine Liste und benutzt ab
//       `cEntitySameParticleMax` Exemplaren das aelteste wieder. Mit `-1` gibt es KEINE
//       Obergrenze: jeder Schuss instanziiert ein neues Exemplar.
//   (2) Aufgeraeumt wird nur, was sich selbst aufraeumt. `gunfire_pistol` ist ein Einmaleffekt
//       und verschwindet von allein; `flame_hazard` ist der Dauerbrenner der Rohr-Hazards
//       (blocks.xml, `pipeFireHazard*`) und laeuft endlos. Vier Schuss pro Sekunde ergaben
//       damit vier neue, nie endende Flammen pro Sekunde.
//   (3) Ohne Elternteil landet das Exemplar an `ParticleEffect.RootT` (IL_0377), also an einem
//       globalen Knoten mit Weltposition und Weltdrehung von genau EINEM Zeitpunkt. Es dreht
//       sich nicht mit, und es verschwindet nicht mit dem Turret.
//
// Deshalb macht diese Klasse den Effekt selbst, und zwar mit genau der Mechanik, die die
// vanilla-Aktion `AttachParticleEffectToEntity` fuer dauerhafte Partikel an Entities benutzt
// (IL_029A: `SetParent(parent, false)` + lokaler Offset + lokale Drehung): EIN Exemplar, Kind des
// `Muzzle`-Transforms. Dass es ein Kind ist, erledigt drei Dinge gratis - es dreht sich mit dem
// Rohr, es ueberlebt eine Weltursprungsverschiebung, und es wird mit dem Modell des Turrets
// zerstoert. Ein Ueberbleibsel nach dem Abbau ist damit strukturell nicht mehr moeglich.
//
// Der Ton laeuft parallel: `flametrap_lp` ist als Schleife deklariert (sounds.xml:12535,
// `Loop="true"`), also einmal starten und einmal stoppen statt 4x/s neu anspielen.
public static class TurretFlameJet
{
    // Diagnoseausgabe. VOR DEM RELEASE AUF false - eine Zeile pro Turret, kein Dauerlog.
    private const bool DBG = true;

    // Alles an der entity_class einstellbar, damit eine schief stehende Flamme eine
    // XML-Korrektur ist und kein Rebuild.
    private const string PropParticle   = "AgentFlameJet";
    private const string PropOffset     = "AgentFlameJetOffset";
    private const string PropRotation   = "AgentFlameJetRotation";
    private const string PropScale      = "AgentFlameJetScale";
    private const string PropLoopSound  = "AgentFlameJetSound";
    private const string PropStartSound = "AgentFlameJetStartSound";
    private const string PropStopSound  = "AgentFlameJetStopSound";

    // Name des erzeugten Transforms und Schluessel in `EntityAlive.particles`.
    private const string ChildName = "AgentFlameJet";

    // Wie lange die Flamme nach dem letzten Schuss noch brennt. Muss ueber dem Schussabstand
    // liegen, sonst flackert sie im Takt der Feuerrate - genau das Stroboskop, das wir loswerden
    // wollen. Bei 240 Schuss/Minute sind das 0.25 s Abstand.
    private const float LingerSeconds = 0.45f;

    private sealed class Jet
    {
        public Transform T;             // das erzeugte Exemplar, Kind von Muzzle
        public ParticleSystem[] Systems;
        public float LastFire;
        public bool Burning;
        public string LoopSound, StartSound, StopSound;
    }

    private static readonly Dictionary<int, Jet> jets = new Dictionary<int, Jet>();
    // Turrets, bei denen das Anlegen fehlgeschlagen ist - kein zweiter Versuch. `Fire` laeuft
    // mehrmals pro Sekunde durch jedes Turret der Welt; eine Fehlermeldung pro Schuss waere
    // schlimmer als der Fehler.
    private static readonly HashSet<int> failed = new HashSet<int>();
    // Pro entity_class, nicht pro Turret: die Antwort haengt am XML, nicht am Exemplar.
    private static readonly Dictionary<string, bool> configuredClasses =
        new Dictionary<string, bool>();

    // Hat diese Turretsorte eine eigene Flamme? Der Fire-Patch braucht die Antwort, um vanillas
    // Muendungseffekt genau dort abzuschalten, wo wir ihn ersetzen - und nirgends sonst.
    public static bool IsConfigured(EntityTurret turret)
    {
        var cls = turret != null ? turret.EntityClass : null;
        if (cls == null) return false;

        string key = cls.entityClassName;
        bool hit;
        if (key != null && configuredClasses.TryGetValue(key, out hit)) return hit;

        var values = cls.Properties != null ? cls.Properties.Values : null;
        string particle;
        hit = values != null && values.TryGetValue(PropParticle, out particle)
              && !string.IsNullOrEmpty(particle);
        if (key != null) configuredClasses[key] = hit;
        return hit;
    }

    // ------------------------------------------------------------------------------------
    // Aus dem Fire-Patch: es wurde gerade geschossen.
    // ------------------------------------------------------------------------------------
    public static void NotifyFired(EntityTurret turret, Transform muzzle)
    {
        if (turret == null) return;

        int id = turret.entityId;
        Jet jet;
        if (!jets.TryGetValue(id, out jet))
        {
            if (failed.Contains(id) || !IsConfigured(turret)) return;
            jet = Create(turret, muzzle);
            if (jet == null)
            {
                failed.Add(id);
                return;
            }
            jets[id] = jet;
        }

        jet.LastFire = Time.time;
        if (!jet.Burning) SetBurning(turret, jet, true);
    }

    // ------------------------------------------------------------------------------------
    // Aus dem Laufzeit-Patch, jeden Frame: brennt sie noch zurecht?
    // ------------------------------------------------------------------------------------
    public static void Tick(EntityTurret turret)
    {
        if (turret == null) return;

        Jet jet;
        if (!jets.TryGetValue(turret.entityId, out jet)) return;

        // Das Exemplar haengt am Modell des Turrets. Ist das Modell weg (Chunk entladen,
        // Entity zerstoert), ist auch das Exemplar weg - aber der SCHLEIFENTON haengt an der
        // Entity-Id und wuerde weiterlaufen. Deshalb wird hier auch der Fall abgeraeumt, den
        // `Forget` nicht sieht, weil er nicht durch unseren Abbau ging.
        if (jet.T == null)
        {
            StopSound(turret.entityId, jet);
            jets.Remove(turret.entityId);
            return;
        }

        if (!jet.Burning) return;

        if (turret.IsDead() || !turret.IsOn || Time.time - jet.LastFire > LingerSeconds)
            SetBurning(turret, jet, false);
    }

    // ------------------------------------------------------------------------------------
    // Aus dem Abbau-Pfad. Das Partikelexemplar stirbt mit dem Modell, der Ton nicht.
    // ------------------------------------------------------------------------------------
    public static void Forget(EntityTurret turret)
    {
        if (turret == null) return;

        int id = turret.entityId;
        Jet jet;
        if (jets.TryGetValue(id, out jet))
        {
            StopSound(id, jet);
            // Ueber `RemoveParticle`, nicht per Destroy: das nimmt es in einem Schritt aus
            // `EntityAlive.particles` UND zerstoert das GameObject (IL_0010..IL_001E).
            turret.RemoveParticle(ChildName);
            jets.Remove(id);
        }
        failed.Remove(id);
    }

    // ------------------------------------------------------------------------------------

    private static Jet Create(EntityTurret turret, Transform muzzle)
    {
        var cls = turret.EntityClass;
        var values = cls != null && cls.Properties != null ? cls.Properties.Values : null;
        if (values == null) return null;

        string particle;
        if (!values.TryGetValue(PropParticle, out particle) || string.IsNullOrEmpty(particle))
            return null;

        if (muzzle == null)
        {
            Debug.LogWarning("[AgentArmory] Flammenturret " + turret.entityId
                + " hat kein Muzzle-Transform - keine Flamme.");
            return null;
        }

        // Derselbe Weg, den `ParticleEffect.SpawnParticleEffect` fuer dynamische Partikel geht
        // (IL_01AC..IL_01B2): Id aus dem Namen, Prefab aus `loadedTs`. Ist der Name unbekannt,
        // loggt `GetDynamicTransform` selbst einen Fehler und liefert null.
        var prefab = ParticleEffect.GetDynamicTransform(ParticleEffect.ToId(particle));
        if (prefab == null) return null;

        var t = Object.Instantiate(prefab);
        if (t == null) return null;

        t.name = ChildName;
        // Wie die vanilla-Attach-Aktion (IL_0288..IL_0295): erst auf die Ebene des Elternteils,
        // dann anhaengen. Ein Partikel auf der falschen Layer wird weggeblendet.
        Utils.SetLayerRecursively(t.gameObject, muzzle.gameObject.layer);
        t.SetParent(muzzle, false);
        t.localPosition = ReadVector(values, PropOffset, Vector3.zero);
        t.localRotation = Quaternion.Euler(ReadVector(values, PropRotation, Vector3.zero));
        float scale = ReadFloat(values, PropScale, 1f);
        if (scale > 0f && !Mathf.Approximately(scale, 1f)) t.localScale = Vector3.one * scale;

        // Damit `RemoveParticle` es findet und vanilla von ihm weiss.
        turret.AddParticle(ChildName, t);

        var jet = new Jet
        {
            T          = t,
            Systems    = t.GetComponentsInChildren<ParticleSystem>(),
            LastFire   = Time.time,
            Burning    = false,
            LoopSound  = ReadString(values, PropLoopSound,  null),
            StartSound = ReadString(values, PropStartSound, null),
            StopSound  = ReadString(values, PropStopSound,  null),
        };

        // Erzeugt wird beim ersten Schuss, also brennt es sofort - aber `playOnAwake` des Prefabs
        // hat schon gezuendet, bevor Offset und Drehung standen. Einmal auf Anfang.
        StopSystems(jet);

        if (DBG)
            Debug.Log(string.Format(
                "[AgentArmoryFlame] turret {0} Flamme '{1}' an {2}: {3} Systeme, lokal pos={4} rot={5} scale={6}",
                turret.entityId, particle, muzzle.name, jet.Systems == null ? 0 : jet.Systems.Length,
                t.localPosition.ToString("F2"), t.localEulerAngles.ToString("F0"), scale));

        return jet;
    }

    private static void SetBurning(EntityTurret turret, Jet jet, bool on)
    {
        jet.Burning = on;

        if (on)
        {
            PlaySystems(jet);
            if (!string.IsNullOrEmpty(jet.StartSound))
                Audio.Manager.Play(turret, jet.StartSound, 1f, false);
            if (!string.IsNullOrEmpty(jet.LoopSound))
                Audio.Manager.Play(turret, jet.LoopSound, 1f, false);
            return;
        }

        StopSystems(jet);
        StopSound(turret.entityId, jet);
        if (!string.IsNullOrEmpty(jet.StopSound))
            Audio.Manager.Play(turret, jet.StopSound, 1f, false);
    }

    // `StopEmitting` statt `StopEmittingAndClear`: die schon fliegenden Partikel brennen aus,
    // die Flamme reisst also nicht schlagartig ab.
    private static void StopSystems(Jet jet)
    {
        if (jet.Systems == null) return;
        for (int i = 0; i < jet.Systems.Length; i++)
            if (jet.Systems[i] != null)
                jet.Systems[i].Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    private static void PlaySystems(Jet jet)
    {
        if (jet.Systems == null) return;
        for (int i = 0; i < jet.Systems.Length; i++)
            if (jet.Systems[i] != null)
                jet.Systems[i].Play(true);
    }

    private static void StopSound(int entityId, Jet jet)
    {
        if (!string.IsNullOrEmpty(jet.LoopSound))
            Audio.Manager.Stop(entityId, jet.LoopSound);
    }

    // ------------------------------------------------------------------------------------

    private static string ReadString(Dictionary<string, string> values, string key, string fallback)
    {
        string raw;
        return values.TryGetValue(key, out raw) && !string.IsNullOrEmpty(raw) ? raw : fallback;
    }

    // StringParsers, nicht float.TryParse: invariant, also unabhaengig von der Systemsprache.
    private static Vector3 ReadVector(Dictionary<string, string> values, string key, Vector3 fallback)
    {
        string raw;
        if (!values.TryGetValue(key, out raw) || string.IsNullOrEmpty(raw)) return fallback;
        Vector3 parsed;
        return StringParsers.TryParseVector3(raw, out parsed) ? parsed : fallback;
    }

    private static float ReadFloat(Dictionary<string, string> values, string key, float fallback)
    {
        string raw;
        if (!values.TryGetValue(key, out raw)) return fallback;
        float parsed;
        return StringParsers.TryParseFloat(raw, out parsed) ? parsed : fallback;
    }
}
