using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Bindet die Dauerflamme an den Schuss - und schaltet vanillas Muendungseffekt genau dort ab,
// wo wir ihn ersetzen.
//
// ⚠ WARUM DAS ABSCHALTEN IN C# STEHT UND NICHT IM XML. Naheliegend waere
// `<property name="ParticlesMuzzleFire" value=""/>` an `agentTurretFlame`. Der Effekt haengt dann
// aber davon ab, wie `DynamicProperties` einen LEEREN Wert gegen den geerbten von
// `junkTurretGun` verrechnet - und das ist eine Annahme, die genauso teuer war, als sie beim
// Seeker zweimal danebenlag. Das Feld direkt zu leeren ist dagegen nachweisbar wirksam:
// `Fire` prueft an IL_0503 `string.IsNullOrEmpty(muzzleFireParticle)` und ueberspringt bei leer
// den ganzen Block - Partikel UND Schussgeraeusch, beide stecken in derselben
// `new ParticleEffect(...)`-Zeile.
//
// Genau deshalb steht am Flammenturret auch kein `FireSound`: es waere nicht zu hoeren. Der Ton
// kommt aus `TurretFlameJet`, als Schleife.
//
// Die Felder sind Instanzfelder des Fire-Controllers, und jedes Turret hat seinen eigenen. Das
// Leeren trifft also nur dieses Exemplar - kein anderes Turret im Spiel merkt etwas davon.
[HarmonyPatch(typeof(MiniTurretFireController), "Fire")]
public static class Patch_TurretFlameJet
{
    private static FieldInfo fTurret, fMuzzle, fParticle, fSound;

    private static void EnsureFields()
    {
        if (fTurret != null) return;
        fTurret   = AccessTools.Field(typeof(MiniTurretFireController), "entityTurret");
        fMuzzle   = AccessTools.Field(typeof(MiniTurretFireController), "Muzzle");
        fParticle = AccessTools.Field(typeof(MiniTurretFireController), "muzzleFireParticle");
        fSound    = AccessTools.Field(typeof(MiniTurretFireController), "fireSound");
    }

    private static EntityTurret AgentTurretOf(MiniTurretFireController ctl)
    {
        EnsureFields();
        var turret = fTurret != null ? fTurret.GetValue(ctl) as EntityTurret : null;
        if (turret == null || !Patch_TurretRuntime.IsAgentTurret(turret)) return null;
        return TurretFlameJet.IsConfigured(turret) ? turret : null;
    }

    // Vor dem Schuss: vanillas Muendungseffekt aus. Muss ein Prefix sein - im Postfix waere das
    // Exemplar fuer diesen Schuss schon in der Welt.
    static void Prefix(MiniTurretFireController __instance)
    {
        if (__instance == null) return;
        var turret = AgentTurretOf(__instance);
        if (turret == null) return;

        if (fParticle != null && !string.IsNullOrEmpty(fParticle.GetValue(__instance) as string))
            fParticle.SetValue(__instance, string.Empty);
        if (fSound != null && !string.IsNullOrEmpty(fSound.GetValue(__instance) as string))
            fSound.SetValue(__instance, string.Empty);
    }

    // Nach dem Schuss: Flamme an bzw. am Leben halten. Nach dem Schuss, damit ein Schuss, den
    // `Fire` selbst noch verwirft (IL_0027: `IsOn` false), keine Flamme entzuendet.
    static void Postfix(MiniTurretFireController __instance)
    {
        if (__instance == null) return;
        var turret = AgentTurretOf(__instance);
        if (turret == null) return;

        var muzzle = fMuzzle != null ? fMuzzle.GetValue(__instance) as Transform : null;
        TurretFlameJet.NotifyFired(turret, muzzle);
    }
}
