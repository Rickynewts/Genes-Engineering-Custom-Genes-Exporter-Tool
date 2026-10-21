using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using RimWorld;
using Verse;

namespace CustomGeneExporter
{
    //基因类型
    public enum GeneColorType
    {
        None = 0,
        SkinColor = 1,
        HairColor = 2
    }

    //待导出的基因
    public class GeneExportSpec
    {
        public string Name = "";
        public string Description = "";
        public int Metabolism;
        public int Complexity;

        public GeneColorType ColorType = GeneColorType.None;
        public int ColorR = 200;
        public int ColorG = 160;
        public int ColorB = 120;

        public string ImagePath = "";
        public GeneCategoryDef Category;
        public Type GeneClass;
        public ModContentPack GeneClassMod;

        public List<NeedDef> DisablesNeeds = new List<NeedDef>();
        public List<StatModifier> StatFactors = new List<StatModifier>();
        public List<StatModifier> StatOffsets = new List<StatModifier>();

        //导出时算出来的图标路径
        public string ResolvedIconPath;
    }

    public static class GeneExporter
    {
        private static string storageModFolderName = "ExportedCustomGenesStorage";

        private static string StorageFolder
        {
            get { return Path.Combine(GenFilePaths.ModsFolderPath, storageModFolderName); }
        }

        private static string GeneDefsFolder
        {
            get { return Path.Combine(StorageFolder, "Defs", "GeneDefs"); }
        }

        //供对话框统计基因数量
        public static string GeneDefsFolderPath
        {
            get { return GeneDefsFolder; }
        }

        //基因XML文件名
        public static string GetGeneFileName(string geneName)
        {
            return "GegeXT_" + (geneName ?? "").Replace(" ", "") + ".xml";
        }

        //查defName冲突
        public static List<string> FindGeneNameConflicts(string modPackageId, List<GeneExportSpec> genes)
        {
            List<string> conflicts = new List<string>();
            if (genes == null)
            {
                return conflicts;
            }

            string defsFolder = GeneDefsFolder;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GeneExportSpec gene in genes)
            {
                if (gene == null || string.IsNullOrEmpty(gene.Name))
                {
                    continue;
                }

                string file = GetGeneFileName(gene.Name);
                bool duplicatedInBatch = !seen.Add(file);
                bool existsOnDisk = File.Exists(Path.Combine(defsFolder, file));
                if ((duplicatedInBatch || existsOnDisk) && !conflicts.Contains(gene.Name))
                {
                    conflicts.Add(gene.Name);
                }
            }

            return conflicts;
        }

        //批量导出
        public static void ExportGenesToModFolder(
            string modName, string modAuthor, string modPackageId,
            List<GeneExportSpec> genes, bool overwriteExisting, string previewImagePath)
        {
            try
            {
                List<GeneExportSpec> valid = new List<GeneExportSpec>();
                if (genes != null)
                {
                    foreach (GeneExportSpec gene in genes)
                    {
                        if (gene != null && !string.IsNullOrEmpty(gene.Name) && gene.Name.Trim().Length > 0)
                        {
                            valid.Add(gene);
                        }
                    }
                }

                if (valid.Count == 0)
                {
                    Messages.Message("CGET_NoGenesToExport".Translate(), MessageTypeDefOf.ThreatBig, false);
                    return;
                }

                // 格式化 packageId
                string cleanPackageId = (modPackageId ?? "").ToLower().Trim().Replace(" ", "_");
                if (string.IsNullOrEmpty(cleanPackageId))
                {
                    cleanPackageId = "player.customgenes";
                }

                string storageFolder = StorageFolder;
                string aboutFolder = Path.Combine(storageFolder, "About");
                string defsFolder = Path.Combine(storageFolder, "Defs", "GeneDefs");
                string texturesFolder = Path.Combine(storageFolder, "Textures", "CustomGenes");

                Directory.CreateDirectory(aboutFolder);
                Directory.CreateDirectory(defsFolder);
                Directory.CreateDirectory(texturesFolder);

                // Mod 列表里显示的预览图：About\Preview.png
                EnsureModPreview(aboutFolder, previewImagePath);

                //图标
                string defaultIconTexturePath = null;
                bool defaultIconPrepared = false;

                foreach (GeneExportSpec gene in valid)
                {
                    string imagePath = NormalizePath(gene.ImagePath);
                    if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                    {
                        string fileName = Path.GetFileName(imagePath);
                        File.Copy(imagePath, Path.Combine(texturesFolder, fileName), true);
                        gene.ResolvedIconPath = "CustomGenes/" + Path.GetFileNameWithoutExtension(fileName);
                    }
                    else if (gene.ColorType != GeneColorType.None)
                    {
                        //继承原版父节点的图标
                        gene.ResolvedIconPath = null;
                    }
                    else
                    {
                        if (!defaultIconPrepared)
                        {
                            defaultIconPrepared = true;
                            defaultIconTexturePath = EnsureDefaultIcon(texturesFolder);
                        }
                        gene.ResolvedIconPath = defaultIconTexturePath;
                    }
                }

                //About.xml
                bool anyColorGene = valid.Any(g => g.ColorType != GeneColorType.None);
                List<ModContentPack> classMods = new List<ModContentPack>();
                foreach (GeneExportSpec gene in valid)
                {
                    if (gene.GeneClassMod != null
                        && !classMods.Any(m => m.PackageId == gene.GeneClassMod.PackageId))
                    {
                        classMods.Add(gene.GeneClassMod);
                    }
                }
                GenerateAboutFile(aboutFolder, modName, modAuthor, cleanPackageId, classMods, anyColorGene);

                //自动生成的分类
                string autoCategoryDefName = cleanPackageId.Replace(".", "_").Replace("-", "_") + "_Category";
                bool autoCategoryWritten = false;

                int written = 0;
                int skipped = 0;
                foreach (GeneExportSpec gene in valid)
                {
                    string fullPath = Path.Combine(defsFolder, GetGeneFileName(gene.Name));

                    if (!overwriteExisting && File.Exists(fullPath))
                    {
                        skipped++;
                        continue;
                    }

                    if (gene.ColorType != GeneColorType.None)
                    {
                        //继承父节点的分类
                        string colorCategoryDefName = gene.Category == null ? null : gene.Category.defName;
                        bool isSkin = gene.ColorType == GeneColorType.SkinColor;
                        GenerateColorGeneDefFile(defsFolder, gene.Name, gene.Description ?? "", gene.Metabolism, gene.Complexity,
                            gene.ResolvedIconPath, colorCategoryDefName, gene.Category, cleanPackageId,
                            gene.ColorR, gene.ColorG, gene.ColorB,
                            isSkin ? "GeneSkinColorOverride" : "GeneHairColorBase",
                            isSkin ? "skinColorOverride" : "hairColorOverride");
                    }
                    else
                    {
                        string categoryDefName;
                        if (gene.Category == null)
                        {
                            categoryDefName = autoCategoryDefName;
                            if (!autoCategoryWritten)
                            {
                                autoCategoryWritten = true;
                                GenerateGeneCategoryFile(defsFolder, autoCategoryDefName, "CGET_DefaultCategory".Translate());
                            }
                        }
                        else
                        {
                            categoryDefName = gene.Category.defName;
                        }

                        GenerateGeneDefFile(defsFolder, gene.Name, gene.Description ?? "", gene.Metabolism, gene.Complexity,
                            gene.DisablesNeeds, gene.StatFactors, gene.StatOffsets, gene.ResolvedIconPath,
                            gene.GeneClass, gene.GeneClassMod, categoryDefName, gene.Category, cleanPackageId);
                    }

                    written++;
                }

                string message = skipped > 0
                    ? "CGET_ExportSuccessSkipped".Translate(written, storageModFolderName, skipped)
                    : "CGET_ExportSuccess".Translate(written, storageModFolderName);
                Messages.Message(message, MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception ex)
            {
                Messages.Message("CGET_ExportFailed".Translate(ex.Message), MessageTypeDefOf.ThreatBig, false);
                Log.Error("[CustomGeneExporter] " + ex.ToString());
            }
        }

        //默认图标复制进导出的 Mod
        private static string EnsureDefaultIcon(string texturesFolder)
        {
            string defaultIconSource = GetOwnDefaultIconPath();
            if (!string.IsNullOrEmpty(defaultIconSource))
            {
                File.Copy(defaultIconSource, Path.Combine(texturesFolder, "DefaultGeneIcon.png"), true);
                return "CustomGenes/DefaultGeneIcon";
            }

            Log.Warning("[CustomGeneExporter] 未找到 Textures\\CustomGeneExporter\\DefaultIcon.png，图标仍指向本框架 Mod。");
            return "CustomGeneExporter/DefaultIcon";
        }

        // Mod 列表里的预览图 = About\Preview.png：
        // 优先用玩家填的自定义预览图，否则把本框架的 DefaultPreview.png 复制一份进导出 Mod
        private static void EnsureModPreview(string aboutFolder, string previewImagePath)
        {
            string destination = Path.Combine(aboutFolder, "Preview.png");
            try
            {
                // 玩家可能直接粘贴带双引号的路径
                previewImagePath = NormalizePath(previewImagePath);
                if (!string.IsNullOrEmpty(previewImagePath) && File.Exists(previewImagePath))
                {
                    File.Copy(previewImagePath, destination, true);
                    return;
                }

                string defaultPreview = GetOwnDefaultPreviewPath();
                if (!string.IsNullOrEmpty(defaultPreview))
                {
                    File.Copy(defaultPreview, destination, true);
                }
                else
                {
                    Log.Warning("[CustomGeneExporter] 未找到 Textures\\CustomGeneExporter\\DefaultPreview.png，导出的 Mod 没有预览图。");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[CustomGeneExporter] 复制预览图失败: " + ex.Message);
            }
        }

        private static void GenerateAboutFile(string aboutFolder, string modName, string modAuthor, string modPackageId, List<ModContentPack> dependencyMods, bool requireBiotech)
        {
            string fileName = Path.Combine(aboutFolder, "About.xml");

            XElement modMetaData = new XElement("ModMetaData",
                new XElement("name", string.IsNullOrEmpty(modName) ? (string)"CGET_DefaultModName".Translate() : modName),
                new XElement("author", string.IsNullOrEmpty(modAuthor) ? (string)"CGET_DefaultModAuthor".Translate() : modAuthor),
                new XElement("packageId", modPackageId),
                new XElement("supportedVersions", new XElement("li", "1.5"), new XElement("li", "1.6")),
                new XElement("description", (string)"CGET_DefaultModDescription".Translate())
            );

            List<XElement> dependencies = new List<XElement>();
            List<XElement> loadAfter = new List<XElement>();
            HashSet<string> addedPackageIds = new HashSet<string>();

            if (dependencyMods != null)
            {
                foreach (ModContentPack dependency in dependencyMods)
                {
                    if (dependency == null || string.IsNullOrEmpty(dependency.PackageId) || !addedPackageIds.Add(dependency.PackageId))
                    {
                        continue;
                    }

                    dependencies.Add(new XElement("li",
                        new XElement("packageId", dependency.PackageId),
                        new XElement("displayName", dependency.Name)
                    ));
                    loadAfter.Add(new XElement("li", dependency.PackageId));
                }
            }

            //声明依赖
            const string biotechPackageId = "ludeon.rimworld.biotech";
            if (requireBiotech && addedPackageIds.Add(biotechPackageId))
            {
                dependencies.Add(new XElement("li",
                    new XElement("packageId", biotechPackageId),
                    new XElement("displayName", "Biotech")
                ));
                loadAfter.Add(new XElement("li", biotechPackageId));
            }

            if (dependencies.Count > 0)
            {
                modMetaData.Add(new XElement("modDependencies", dependencies));
                modMetaData.Add(new XElement("loadAfter", loadAfter));
            }

            XDocument aboutDoc = new XDocument(modMetaData);
            aboutDoc.Save(fileName);
        }
        // 本 Mod 的根目录。
        // 注意：RimWorld 用 Assembly.Load(byte[]) 加载 Mod 程序集，Assembly.Location 是空字符串，
        // 所以不能靠程序集路径反推，必须从「已加载 Mod 列表」里反查是哪个 Mod 加载了我们。
        private static string GetOwnModRoot()
        {
            try
            {
                Assembly selfAssembly = typeof(GeneExporter).Assembly;
                List<ModContentPack> runningMods = LoadedModManager.RunningModsListForReading;

                foreach (ModContentPack mod in runningMods)
                {
                    if (mod == null || mod.assemblies == null || mod.assemblies.loadedAssemblies == null)
                    {
                        continue;
                    }

                    foreach (Assembly assembly in mod.assemblies.loadedAssemblies)
                    {
                        if (assembly == selfAssembly)
                        {
                            return mod.RootDir;
                        }
                    }
                }

                foreach (ModContentPack mod in runningMods)
                {
                    if (mod != null && string.Equals(mod.PackageId, "yushiren.genesengineering", StringComparison.OrdinalIgnoreCase))
                    {
                        return mod.RootDir;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[CustomGeneExporter] 无法从 Mod 列表定位本 Mod 目录: " + ex.Message);
            }

            try
            {
                string location = typeof(GeneExporter).Assembly.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    return Path.GetDirectoryName(Path.GetDirectoryName(location));
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        private static string GetOwnDefaultIconPath()
        {
            try
            {
                string modRoot = GetOwnModRoot();
                if (string.IsNullOrEmpty(modRoot))
                {
                    return null;
                }

                string iconPath = Path.Combine(modRoot, "Textures", "CustomGeneExporter", "DefaultIcon.png");
                return File.Exists(iconPath) ? iconPath : null;
            }
            catch (Exception ex)
            {
                Log.Warning("[CustomGeneExporter] 无法定位默认图标: " + ex.Message);
                return null;
            }
        }

        //默认预览图（Textures\CustomGeneExporter\DefaultPreview.png）
        private static string GetOwnDefaultPreviewPath()
        {
            try
            {
                string modRoot = GetOwnModRoot();
                if (string.IsNullOrEmpty(modRoot))
                {
                    return null;
                }

                string previewPath = Path.Combine(modRoot, "Textures", "CustomGeneExporter", "DefaultPreview.png");
                return File.Exists(previewPath) ? previewPath : null;
            }
            catch (Exception ex)
            {
                Log.Warning("[CustomGeneExporter] 无法定位默认预览图: " + ex.Message);
                return null;
            }
        }

        private static void GenerateGeneCategoryFile(string defsFolder, string categoryDefName, string label)
        {
            string folder = Path.Combine(defsFolder, "GeneCategoryDefs");
            Directory.CreateDirectory(folder);
            string fileName = Path.Combine(folder, categoryDefName + ".xml");
            int xenotypePriority = PickFreePriority(categoryDefName, StableHash(categoryDefName), d => d.displayPriorityInXenotype);
            int genepackPriority = PickFreePriority(categoryDefName, StableHash(categoryDefName + "_genepack"), d => d.displayPriorityInGenepack);

            XElement catDef = new XElement("GeneCategoryDef",
                new XElement("defName", categoryDefName),
                new XElement("label", label),
                new XElement("displayPriorityInXenotype", xenotypePriority),
                new XElement("displayPriorityInGenepack", genepackPriority)
            );

            XDocument doc = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Defs", catDef)
            );
            doc.Save(fileName);
        }
        private static int PickFreePriority(string ignoreDefName, int seed, Func<GeneCategoryDef, float> selector)
        {
            HashSet<float> used = new HashSet<float>();
            foreach (GeneCategoryDef def in DefDatabase<GeneCategoryDef>.AllDefsListForReading)
            {
                if (def.defName == ignoreDefName)
                {
                    continue;
                }
                used.Add(selector(def));
            }

            int offset = seed % 100;
            for (int i = 0; i < 100; i++)
            {
                int candidate = 900 + ((offset + i) % 100);
                if (!used.Contains(candidate))
                {
                    return candidate;
                }
            }
            return -1000;
        }

        private static int StableHash(string text)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in text)
                {
                    hash = hash * 31 + c;
                }
                return hash & 0x7FFFFFFF;
            }
        }
        private static void GenerateColorGeneDefFile(
            string defsFolder, string name, string desc, int met, int cpx, string iconPath,
            string categoryDefName, GeneCategoryDef selectedCategory, string ownPackageId,
            int r, int g, int b, string parentName, string colorElementName)
        {
            string fileName = Path.Combine(defsFolder, "GegeXT_" + name.Replace(" ", "") + ".xml");

            XElement geneDef = new XElement("GeneDef",
                new XAttribute("ParentName", parentName),
                new XAttribute("MayRequire", "ludeon.rimworld.biotech"));

            geneDef.Add(
                new XElement("defName", "GegeCG_" + name.Replace(" ", "")),
                new XElement("label", name),
                new XElement("description", desc),
                new XElement("biostatMet", met),
                new XElement("biostatCpx", cpx),
                new XElement(colorElementName, FormatColor(r, g, b))
            );

            if (!string.IsNullOrEmpty(iconPath))
            {
                geneDef.Add(new XElement("iconPath", iconPath));
            }

            if (!string.IsNullOrEmpty(categoryDefName))
            {
                XElement categoryElement = new XElement("displayCategory", categoryDefName);
                if (selectedCategory != null
                    && selectedCategory.modContentPack != null
                    && !selectedCategory.modContentPack.IsCoreMod
                    && selectedCategory.modContentPack.PackageId != ownPackageId)
                {
                    categoryElement.Add(new XAttribute("MayRequire", selectedCategory.modContentPack.PackageId));
                }
                geneDef.Add(categoryElement);
            }

            XDocument doc = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Defs", geneDef));
            doc.Save(fileName);
        }

        //输出分数形式
        public static string FormatColor(int r, int g, int b)
        {
            return string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2})",
                (ClampColorChannel(r) / 255f).ToString("0.###", CultureInfo.InvariantCulture),
                (ClampColorChannel(g) / 255f).ToString("0.###", CultureInfo.InvariantCulture),
                (ClampColorChannel(b) / 255f).ToString("0.###", CultureInfo.InvariantCulture));
        }

        private static int ClampColorChannel(int value)
        {
            if (value < 0)
            {
                return 0;
            }
            if (value > 255)
            {
                return 255;
            }
            return value;
        }
        //统一去掉首尾引号
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            string result = path.Trim();
            if (result.Length >= 2 && result[0] == '"' && result[result.Length - 1] == '"')
            {
                result = result.Substring(1, result.Length - 2).Trim();
            }

            return result;
        }

        private static void GenerateGeneDefFile(
    string defsFolder, string name, string desc, int met, int cpx,
    List<NeedDef> disablesNeeds, List<StatModifier> factors, List<StatModifier> offsets, string iconPath,
    Type selectedGeneClass, ModContentPack selectedGeneClassMod,
            string categoryDefName, GeneCategoryDef selectedCategory, string ownPackageId)
        {
            string fileName = Path.Combine(defsFolder, "GegeXT_" + name.Replace(" ", "") + ".xml");

            XElement geneDef = new XElement("GeneDef");

            if (selectedGeneClass != null && selectedGeneClassMod != null)
            {
                geneDef.Add(new XAttribute("MayRequire", selectedGeneClassMod.PackageId));
            }

            // displayCategory 节点：
            XElement categoryElement = new XElement("displayCategory", categoryDefName);
            if (selectedCategory != null
                && selectedCategory.modContentPack != null
                && !selectedCategory.modContentPack.IsCoreMod
                && selectedCategory.modContentPack.PackageId != ownPackageId)
            {
                categoryElement.Add(new XAttribute("MayRequire", selectedCategory.modContentPack.PackageId));
            }

            geneDef.Add(
                new XElement("defName", "GegeCG_" + name.Replace(" ", "")),
                new XElement("label", name),
                new XElement("description", desc),
                new XElement("iconPath", iconPath),
                new XElement("biostatMet", met),
                new XElement("biostatCpx", cpx),
                categoryElement
            );

            if (selectedGeneClass != null && selectedGeneClassMod != null)
            {
                geneDef.Add(new XComment(string.Format(" 使用了来自 Mod [{0}] 的自定义类 ", selectedGeneClassMod.Name)));
                geneDef.Add(new XElement("geneClass", selectedGeneClass.FullName));
            }

            if (selectedGeneClass == null)
            {
                if (disablesNeeds != null && disablesNeeds.Count > 0)
                {
                    XElement disablesNeedsElement = new XElement("disablesNeeds");
                    foreach (NeedDef need in disablesNeeds)
                    {
                        XElement li = new XElement("li", need.defName);
                        if (need.modContentPack != null && !need.modContentPack.IsCoreMod)
                        {
                            li.Add(new XAttribute("MayRequire", need.modContentPack.PackageId));
                        }
                        disablesNeedsElement.Add(li);
                    }
                    geneDef.Add(disablesNeedsElement);
                }

                // 乘算节点
                if (factors != null && factors.Count > 0)
                {
                    XElement statFactorsElement = new XElement("statFactors");
                    foreach (StatModifier factor in factors)
                    {
                        if (factor == null || factor.stat == null)
                        {
                            continue;
                        }

                        XElement statNode = new XElement(factor.stat.defName,
                            FormatStatValue(factor.value));

                        //MayRequire
                        if (factor.stat.modContentPack != null && !factor.stat.modContentPack.IsCoreMod)
                        {
                            statNode.Add(new XAttribute("MayRequire", factor.stat.modContentPack.PackageId));
                        }

                        statFactorsElement.Add(statNode);
                    }
                    if (statFactorsElement.HasElements)
                    {
                        geneDef.Add(statFactorsElement);
                    }
                }

                //加算节点
                if (offsets != null && offsets.Count > 0)
                {
                    XElement statOffsetsElement = new XElement("statOffsets");
                    foreach (StatModifier offset in offsets)
                    {
                        if (offset == null || offset.stat == null)
                        {
                            continue;
                        }

                        XElement statNode = new XElement(offset.stat.defName,
                            FormatStatValue(offset.value));

                        if (offset.stat.modContentPack != null && !offset.stat.modContentPack.IsCoreMod)
                        {
                            statNode.Add(new XAttribute("MayRequire", offset.stat.modContentPack.PackageId));
                        }

                        statOffsetsElement.Add(statNode);
                    }
                    if (statOffsetsElement.HasElements)
                    {
                        geneDef.Add(statOffsetsElement);
                    }
                }
            }

            XDocument defDoc = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Defs", geneDef));
            defDoc.Save(fileName);
        }
        private static string FormatStatValue(float value)
        {
            //格式
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}