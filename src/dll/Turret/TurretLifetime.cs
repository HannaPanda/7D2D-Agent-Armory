using System.Collections.Generic;
using UnityEngine;

// Die Lebenszeit eines Agent-Turrets.
//
// ⚠⚠ KORREKTUR EINER FALSCHEN ANNAHME (2026-08-02). Das hier war zuerst ein Buff
// (`buffAgentTurretLifetime` + `onSelfBuffFinish`), mit der Begruendung "Buffdauern werden mit
// der Entity gespeichert". Das ist FALSCH, und zwar nachweisbar:
//   - `EntityAlive.Write` ruft `Entity.Write` und schreibt danach genau einen Int32. Kein Buff.
//   - `EntityBuffs.Write`/`Read` hat im ganzen Assembly nur ZWEI Aufrufer:
//     `PlayerDataFile.FromPlayer`/`ToPlayer` und `NetPackageEntityStatsBuff`.
//   ⇒ Buffs auf einer Nicht-Spieler-Entity ueberleben keinen Speicherstand.
// Folge im echten Spiel: ein Turret, das einen Neustart ueberlebt, verliert seinen Buff,
// `onSelfBuffFinish` feuert nie und es steht fuer immer da. Genau der Fehler, den der Buff
// verhindern sollte. Belegt im Log vom 2026-08-02: `TurretTracker … loaded 2` in einer
// Sitzung, in der kein Turret geworfen wurde.
//
// RICHTIGER ORT IST DIE ItemValue-METADATA. `EntityTurret.Write` serialisiert
// `OriginalItemValue` (IL_001d), und `ItemValue.Write` schreibt neben `UseTimes`, `Quality`
// und `Meta` auch das `Metadata`-Woerterbuch. `ItemValue.SetMetadata(string, float)` /
// `TryGetMetadata(string, out float)` sind genau dafuer da - ein typisierter, mitgespeicherter
// Schluessel-Wert-Speicher am Item. Kein Zweckentfremden eines Feldes mit eigener Bedeutung
// (`UseTimes` waere die Alternative gewesen, haengt aber ueber `PercentUsesLeft` direkt an
// `IsOn` und haette zwei Mechaniken verkoppelt).
public static class TurretLifetime
{
    // Schluessel in ItemValue.Metadata. Aendern heisst: alle im Umlauf befindlichen Turrets
    // verlieren ihre Uhr und werden beim naechsten Laden abgebaut (siehe Tick).
    public const string Key = "aaTurretLife";

    // Wie oft der Restwert zurueck in die Metadata geschrieben wird. Jeden Frame waere
    // verschwendet - der Wert wird nur beim SPEICHERN gebraucht, und zwei Sekunden
    // Ungenauigkeit sind bei 60 bis 120 Sekunden Laufzeit bedeutungslos.
    private const float PersistInterval = 2f;

    // Laufende Werte der aktuellen Sitzung. Bewusst NICHT die Quelle der Wahrheit fuer das
    // Speichern - nur der Arbeitswert, damit nicht jeder Frame ins Woerterbuch der ItemValue
    // schreibt. Nach einem Ladevorgang ist das hier leer und wird aus der Metadata gefuellt.
    private struct State { public float Remaining; public float LastSeen; public float LastPersist; }
    private static readonly Dictionary<int, State> live = new Dictionary<int, State>();

    public static void Arm(EntityTurret turret, float seconds)
    {
        if (turret == null || turret.OriginalItemValue == null || seconds <= 0f) return;
        turret.OriginalItemValue.SetMetadata(Key, seconds);
    }

    public static void Forget(int entityId)
    {
        live.Remove(entityId);
    }

    // Gibt true zurueck, wenn das Turret abgebaut werden soll.
    //
    // ⚠ Die Zeitmessung laeuft ueber `Time.time`-Differenzen, NICHT ueber `Time.deltaTime`.
    // Grund: `OnUpdateEntity` ist nicht garantiert genau ein Aufruf pro Frame. Waere es ein
    // fester Takt (etwa 20 Hz) bei 60 fps, wuerde eine Summe aus deltaTime nur ein Drittel der
    // echten Zeit ergeben - die Turrets lebten dreimal so lang, und zwar unauffaellig genug,
    // dass es beim Testen als "fuehlt sich lang an" durchgeht. Eine Differenz zweier
    // Zeitstempel ist von der Aufrufhaeufigkeit unabhaengig.
    public static bool Tick(EntityTurret turret)
    {
        var iv = turret.OriginalItemValue;
        if (iv == null) return false;

        int id = turret.entityId;
        float now = Time.time;
        State s;

        if (!live.TryGetValue(id, out s))
        {
            float stored;
            if (!iv.TryGetMetadata(Key, out stored))
            {
                // Keine Uhr: entweder ein Turret aus einer Version vor dieser Korrektur, oder
                // eines, dessen Metadata verloren ging. Beides sind Geraete, die sonst ewig
                // stehen bleiben - also abbauen statt hoffen. Das raeumt insbesondere die
                // Turrets weg, die noch aus der Buff-Fassung in einem Speicherstand liegen.
                return true;
            }
            s = new State { Remaining = stored, LastSeen = now, LastPersist = now };
            live[id] = s;
            return s.Remaining <= 0f;
        }

        // `now - LastSeen` statt deltaTime, siehe oben. Nach oben gedeckelt, damit ein
        // Ladebildschirm oder ein Frame-Aussetzer nicht auf einen Schlag die halbe Laufzeit
        // frisst.
        float dt = Mathf.Clamp(now - s.LastSeen, 0f, 1f);
        s.Remaining -= dt;
        s.LastSeen = now;

        if (s.Remaining <= 0f)
        {
            live.Remove(id);
            return true;
        }

        if (now - s.LastPersist >= PersistInterval)
        {
            // Nur hier wird geschrieben. Ab jetzt traegt ein Speicherstand den Restwert.
            iv.SetMetadata(Key, s.Remaining);
            s.LastPersist = now;
        }

        live[id] = s;
        return false;
    }
}
