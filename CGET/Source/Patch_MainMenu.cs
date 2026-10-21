using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace CustomGeneExporter
{
    [HarmonyPatch(typeof(OptionListingUtility), "DrawOptionListing")]
    public static class Patch_MainMenu
    {
        private const string ButtonLabelKey = "CGET_MainMenuButton";

        [HarmonyPrefix]
        public static void Prefix(ref List<ListableOption> optList)
        {
            if (Current.ProgramState != ProgramState.Entry || optList == null || optList.Count == 0)
            {
                return;
            }

            string buttonLabel = ButtonLabelKey.Translate();

            // 已经加过就跳过
            foreach (var opt in optList)
            {
                if (opt != null && opt.label == buttonLabel)
                {
                    return;
                }
            }
            string creditsLabel = "Credits".Translate();
            string optionsLabel = "Options".Translate();

            int insertIndex = -1;
            int optionsIndex = -1;
            for (int i = 0; i < optList.Count; i++)
            {
                if (optList[i] == null)
                {
                    continue;
                }

                if (optList[i].label == creditsLabel)
                {
                    insertIndex = i;
                    break;
                }

                if (optionsIndex < 0 && optList[i].label == optionsLabel)
                {
                    optionsIndex = i;
                }
            }

            if (insertIndex < 0)
            {
                if (optionsIndex < 0)
                {
                    return;
                }
                insertIndex = optionsIndex + 1;
            }

            optList.Insert(insertIndex, new ListableOption(buttonLabel, delegate
            {
                Find.WindowStack.Add(new Dialog_CreateCustomGene());
            }));
        }
    }
}
