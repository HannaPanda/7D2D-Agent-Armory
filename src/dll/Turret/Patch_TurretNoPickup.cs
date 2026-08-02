using HarmonyLib;

// Agent-Turrets lassen sich nicht aufsammeln - sie sind einmalig bezahlt und laufen ab.
//
// Ohne diese Sperre koennte man das Turret kurz vor Ablauf der Lebenszeit einfach abholen und
// die Restmunition behalten. Das entwertet beide Grenzen auf einmal: die Lebenszeit waere
// folgenlos und die Munition waere wiederverwendbar.
//
// ANGRIFFSPUNKT ist bewusst `CanInteract` und nicht `AllowActivationCommand`:
//   - `CanInteract(int)` hat im ganzen Assembly GENAU EINEN Aufrufer, naemlich
//     `AllowActivationCommand` fuer das Kommando "take" (IL_0015). Ein false hier blockt also
//     exakt das Aufsammeln und sonst nichts.
//   - `AllowActivationCommand` nimmt einen `ReadOnlySpan<char>`. Ein ref struct in einer
//     Harmony-Patchsignatur ist unnoetiges Risiko, wenn ein Aufrufer mit int-Parameter
//     dasselbe leistet.
[HarmonyPatch(typeof(EntityTurret), "CanInteract")]
public static class Patch_TurretNoPickup
{
    static bool Prefix(EntityTurret __instance, ref bool __result)
    {
        if (!Patch_TurretRuntime.IsAgentTurret(__instance)) return true;  // vanilla wie gehabt
        __result = false;
        return false;
    }
}
