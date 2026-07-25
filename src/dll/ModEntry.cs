using System.Reflection;
using HarmonyLib;
using UnityEngine;

// IModApi entry point recognised by the 7DTD mod loader.
public class SeekerClusterMod : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        var harmony = new Harmony("de.hannapanda.seekercluster");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Debug.Log("[SeekerCluster] initialised");
    }
}
