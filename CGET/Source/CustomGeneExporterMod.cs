using HarmonyLib;
using Verse;

namespace CustomGeneExporter
{
    [StaticConstructorOnStartup]
    public static class CustomGeneExporterMod
    {
        static CustomGeneExporterMod()
        {
            new Harmony("Gege.CustomGeneExporter").PatchAll();
        }
    }
}