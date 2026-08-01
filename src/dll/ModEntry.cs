using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// IModApi entry point recognised by the 7DTD mod loader.
//
// The two log lines below are the ONLY things this mod writes on a clean run - the
// debug logging in the Seeker classes is compiled out for release (DBG = false).
// The test bench matches them as its evidence, so keep the wording stable and change
// `test/testbench.mod.json` in the same commit if it ever moves.
public class AgentArmoryMod : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        var harmony = new Harmony("de.hannapanda.agentarmory");
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        // The COUNT is the point, not the fact that PatchAll returned. PatchAll
        // throws when a declared target cannot be resolved, but a patch class that
        // silently matches nothing leaves no trace at all - and that is exactly how
        // a Harmony mod dies on a new game build. Logging the number turns the
        // headless test run into a real check: a build where the count drops is a
        // build where something moved.
        int patched = harmony.GetPatchedMethods().Count();
        Debug.Log("[AgentArmory] Harmony patches applied: " + patched);
        Debug.Log("[AgentArmory] initialised");
    }
}
