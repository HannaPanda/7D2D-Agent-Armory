using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

// "ExplodeOnHit" heisst NICHT "explodiert beim Aufschlag auf den Boden". Es heisst
// "explodiert bei der ersten Beruehrung von irgendwas, in irgendeinem Winkel".
//
// SYMPTOM (Spielbericht 2026-08-03): auf einem ein Block breiten Vorsprung stehen und den
// Cluster Seeker nach unten werfen -> die Ladung bleibt in Kopfhoehe "in der Luft haengen"
// und der Pod baut sich auf dem Tile auf, auf dem der Spieler steht, statt unter ihm. Wer
// weiter weg zielt, hat das Problem nicht.
//
// URSACHE, IL-belegt in drei Schritten:
//   1. `ItemActionThrowAway.throwAway` wirft aus `GetCrosshairPosition3D(0,0,kopf) + 0.23*look`,
//      also praktisch aus der Kamera. Bei steilem Blick nach unten faellt die Ladung damit
//      dicht an der Vorsprungkante entlang.
//   2. `EntityItem.OnCollisionEnter` setzt `Entity.isCollided = true` fuer JEDE Kollision, die
//      `CanCollide` passieren laesst - ohne Pruefung von Normale oder Geschwindigkeit. Die
//      Normalen-/Geschwindigkeitsrechnung weiter unten in der Methode entscheidet nur, ob ein
//      Aufschlaggeraeusch gespielt wird.
//   3. `ItemClassTimeBomb.OnDroppedUpdate` zuendet bei `bExplodeOnHitGround && isCollided`
//      (IL_003d..IL_005c). Ein Streifen der senkrechten Kantenflaeche reicht also vollstaendig
//      aus - und `GameManager.ExplosionServer` bekommt genau diese Position, aus der unsere
//      beiden Postfixe Pod bzw. Turret aufbauen.
// Ein Sicherheitsnetz gibt es dabei NICHT: `FuseTime` wirkt nur ueber `explodeAfterTicks`, und
// das wird ausschliesslich gesetzt, wenn `FuseStartOnDrop` (bei thrownGrenadeContact und
// thrownTimedCharge nicht gesetzt) oder `FusePrimeOnActivate` (bei uns bewusst aus) greift.
// Fuer unsere Wurfladungen ist die Kollision der einzige Zuender.
//
// FIX: fuer Items mit `ExplodeOnLandingOnly=true` zaehlt nur noch eine Beruehrung, bei der die
// Ladung AUF etwas aufkommt. Kriterium ist die Kontaktnormale. Dass sie vom getroffenen Block
// weg zur Ladung hin zeigt, steht im Spielcode selbst: `OnCollisionEnter` rechnet
// `point - 0.25f * normal` und erwartet dort den Block (IL_0124..IL_0168, mit `isair`-Probe und
// Fallback auf `FindMasterBlockForEntityModelBlock`). Deckenkontakt hat also normal.y ~ -1,
// eine Wand ~ 0, ein Boden ~ +1.
//
// Warum ein PREFIX mit `return false` und nicht ein Postfix, der `isCollided` zuruecknimmt:
// `OnCollisionEnter` schickt bei einem Client, der Physics-Master ist, sofort
// `PhysicsMasterSendToServer` los, und `NetPackageEntityPhysics.Setup` packt `isCollided` als
// Flag-Bit 2 mit ein (IL_0033..IL_003f). Ein Postfix kaeme dafuer zu spaet - im Mehrspieler
// wuerde der Server die Ladung trotzdem zuenden.
//
// Preis, bewusst in Kauf genommen: bei einer verworfenen Beruehrung faellt das
// Aufschlaggeraeusch aus (das spielt die Originalmethode am Ende). Die Geschwindigkeitsdaempfung
// aus `CheckStick` (Seeker-Ladung erbt `StickPercent=.5` von thrownGrenadeContact) wird
// dagegen nachgeholt, damit die Ladung nicht ploetzlich lebhafter von Waenden abprallt als
// bisher.
[HarmonyPatch(typeof(EntityItem), "OnCollisionEnter")]
public static class Patch_LandingOnlyCharge
{
    private const bool DBG = false;

    // Marker am Wurfitem. Ohne diese Property aendert sich fuer ein Item gar nichts - jede
    // fremde Granate und jedes fallengelassene Item laeuft unveraendert durch.
    public const string ItemProperty = "ExplodeOnLandingOnly";

    // normal.y einer Flaeche, auf der man aufkommen kann: 0.5 entspricht hoechstens 60 Grad
    // Neigung. Ein 45-Grad-Keil liegt mit ~0.707 klar darueber, eine senkrechte Wand bei 0.
    private const float MinLandingNormalY = 0.5f;

    // Wiederverwendete Liste wie in der Originalmethode - `Collision.contacts` wuerde bei jeder
    // Beruehrung jedes Items der Welt ein Array allokieren.
    private static readonly List<ContactPoint> contacts = new List<ContactPoint>(8);

    static bool Prefix(EntityItem __instance, Collision collision)
    {
        if (__instance == null || collision == null) return true;

        // Schon gezuendet: nichts mehr zu entscheiden.
        if (__instance.isCollided) return true;

        if (!IsLandingOnly(__instance.itemClass)) return true;

        // Dieselbe Vorpruefung wie die Originalmethode in ihrer ersten Zeile. Ohne sie wuerde
        // unten `CheckStick` fuer Beruehrungen laufen, die das Spiel gar nicht bewertet -
        // etwa die mit dem Werfer selbst, den `CanCollide` ueber `belongsPlayerId` ausschliesst.
        if (!__instance.CanCollide(collision)) return true;

        if (HasLandingContact(collision)) return true;

        __instance.CheckStick(collision);
        if (DBG)
            Debug.Log("[AgentArmory] landing-only: Kontakt verworfen bei " + __instance.position);
        return false;
    }

    private static bool IsLandingOnly(ItemClass item)
    {
        if (item == null || item.Properties == null) return false;
        bool value = false;
        // Genau der Weg, auf dem `ItemClassTimeBomb.Init` `ExplodeOnHit` liest: schreibt nur,
        // wenn die Property da ist, und parst invariant.
        item.Properties.ParseBool(ItemProperty, ref value);
        return value;
    }

    private static bool HasLandingContact(Collision collision)
    {
        int count = collision.GetContacts(contacts);
        for (int i = 0; i < count; i++)
            if (contacts[i].normal.y >= MinLandingNormalY) return true;
        return false;
    }
}
