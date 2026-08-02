using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// DIAGNOSE, kein Feature. Beantwortet genau eine offene Frage aus dem ersten Spieltest:
// warum ein Agent-Turret schiesst, aber stumm bleibt.
//
// Ausgangslage (Log vom 2026-08-02): das Turret hat nachweislich gefeuert - 120 Schuss
// verbraucht, "Magazin leer" protokolliert - und war dabei ohne Ton. Der vanilla-Pfad ist
// per IL geprueft und intakt:
//   Fire() -> if (!IsNullOrEmpty(muzzleFireParticle)) { new ParticleEffect(…, fireSound, …);
//             FireControllerUtils.SpawnParticleEffect(pe, -1) }
//   -> GameManager.SpawnParticleEffectClient -> ParticleEffect.SpawnParticleEffect
//   -> PlaySoundInClient -> Audio.Manager.Play(pos, name, -1, false, vol)
// Der Ton wird INNERHALB von SpawnParticleEffect vor jeder Partikelaufloesung gespielt
// (IL_0000..IL_004e), ein ungueltiger Partikelname kann ihn also nicht schlucken - ein
// LEERER dagegen schon, weil dann der ganze Block in Fire uebersprungen wird.
//
// Statisch ist die Kette damit erschoepft. Diese Sonde misst stattdessen, was zur Laufzeit
// wirklich in den drei entscheidenden Werten steht. Das ist dieselbe Methodik, die beim
// Seeker zwei erfolglose IL-Hypothesen abgeloest hat: nicht weiter raten, sondern messen.
//
// ⚠ VOR DEM RELEASE: DBG auf false. Die Sonde ist dann vollstaendig tot (der Compiler
// entfernt den Rumpf), aber die Klasse bleibt als Werkzeug fuer den naechsten Verdachtsfall.
[HarmonyPatch(typeof(MiniTurretFireController), "Fire")]
public static class Patch_TurretFireProbe
{
    private const bool DBG = true;

    // Einmal pro Turret, nicht pro Schuss: bei 500 Schuss/Minute waere alles andere eine
    // Logflut, in der die Antwort untergeht.
    private static readonly HashSet<int> reported = new HashSet<int>();

    private static FieldInfo fSound, fParticle, fMuzzle, fTurret;

    static void Postfix(MiniTurretFireController __instance)
    {
        if (!DBG || __instance == null) return;

        if (fTurret == null)
        {
            // AccessTools statt typeof(...).GetField: die Felder koennen private sein, und
            // AccessTools sucht ohnehin die ganze Basisklassenkette ab.
            fTurret   = AccessTools.Field(typeof(MiniTurretFireController), "entityTurret");
            fSound    = AccessTools.Field(typeof(MiniTurretFireController), "fireSound");
            fParticle = AccessTools.Field(typeof(MiniTurretFireController), "muzzleFireParticle");
            fMuzzle   = AccessTools.Field(typeof(MiniTurretFireController), "Muzzle");
        }

        var turret = fTurret != null ? fTurret.GetValue(__instance) as EntityTurret : null;
        if (turret == null || !Patch_TurretRuntime.IsAgentTurret(turret)) return;
        if (!reported.Add(turret.entityId)) return;

        var muzzle = fMuzzle != null ? fMuzzle.GetValue(__instance) as Transform : null;
        Debug.Log(string.Format(
            "[AgentArmoryFireProbe] turret {0} cls={1} sound='{2}' particle='{3}' muzzle={4} pos={5}",
            turret.entityId,
            turret.EntityClass != null ? turret.EntityClass.entityClassName : "?",
            fSound != null ? (fSound.GetValue(__instance) as string ?? "<null>") : "<field?>",
            fParticle != null ? (fParticle.GetValue(__instance) as string ?? "<null>") : "<field?>",
            muzzle == null ? "NULL" : "ok",
            muzzle == null ? "-" : muzzle.position.ToString("F1")));
    }
}
