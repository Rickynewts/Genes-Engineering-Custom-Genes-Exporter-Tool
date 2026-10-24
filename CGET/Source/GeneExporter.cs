using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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

        //强制获得 / 强制抑制的特性
        public List<TraitDef> ForcedTraits = new List<TraitDef>();
        public List<TraitDef> SuppressedTraits = new List<TraitDef>();

        //导出时算出来的图标路径
        public string ResolvedIconPath;
    }

    public static class GeneExporter
    {
        // defName
        public const string DefNamePrefix = "GegeCG_";
        public const string GeneFilePrefix = "GegeXT_";

        // 一个 packageId 对应一个独立的模组文件夹，避免不同模组互相覆盖 About.xml
        private const string StorageFolderPrefix = "ExportedCustomGenes_";
        // 旧版本用过的固定目录；如果它的 packageId 和当前一致，会继续沿用，避免存档里的模组丢失
        private const string LegacyStorageModFolderName = "ExportedCustomGenesStorage";

        // packageId 只允许小写字母、数字、下划线、点，且至少包含一个点（author.modname）
        private static readonly Regex PackageIdRegex = new Regex(@"^[a-z0-9_]+(\.[a-z0-9_]+)+$", RegexOptions.Compiled);

        // 游戏支持贴图格式
        private static readonly string[] SupportedIconExtensions = { ".png", ".jpg", ".jpeg", ".psd" };

        // 目录解析结果缓存
        private static readonly Dictionary<string, string> ResolvedStorageFolders =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        #region 包名 / defName / 文件

        public static string NormalizePackageId(string packageId)
        {
            string result = (packageId ?? "").ToLowerInvariant().Trim();
            if (result.Length == 0)
            {
                return "";
            }
            while (result.IndexOf("  ", StringComparison.Ordinal) >= 0)
            {
                result = result.Replace("  ", " ");
            }
            return result.Replace(" ", "_");
        }

        // 返回 null 表示合法，否则返回可以直接显示的错误文本
        public static string GetPackageIdError(string packageId)
        {
            string clean = NormalizePackageId(packageId);
            if (clean.Length == 0)
            {
                return "CGET_PackageIdRequired".Translate();
            }
            if (!PackageIdRegex.IsMatch(clean))
            {
                return "CGET_PackageIdInvalid".Translate(clean);
            }
            return null;
        }

        // 只保留字母、数字、下划线（中文算字母），空格和标点全部去掉
        public static string SanitizeDefNameSegment(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "";
            }
            StringBuilder builder = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    builder.Append(c);
                }
            }
            return builder.ToString();
        }

        public static bool IsValidGeneName(string geneName)
        {
            return SanitizeDefNameSegment(geneName).Length > 0;
        }

        public static string GetDefName(string geneName)
        {
            return DefNamePrefix + SanitizeDefNameSegment(geneName);
        }

        //基因XML文件名
        public static string GetGeneFileName(string geneName)
        {
            return GeneFilePrefix + SanitizeDefNameSegment(geneName) + ".xml";
        }

        private static string SafeFolderSegment(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "default";
            }
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            string result = builder.ToString().Trim();
            return result.Length == 0 ? "default" : result;
        }

        public static string GetStorageFolderName(string packageId)
        {
            return Path.GetFileName(GetStorageFolderPath(packageId));
        }

        // 旧目录的 packageId 和当前一致时继续沿用旧目录，保证已经启用的存档不会因为文件夹改名而丢模组；
        // packageId 不同则使用独立的新目录，避免互相覆盖。
        private static string ResolveStorageFolder(string cleanPackageId)
        {
            string cached;
            if (ResolvedStorageFolders.TryGetValue(cleanPackageId, out cached))
            {
                return cached;
            }

            string modsRoot = GenFilePaths.ModsFolderPath;
            string target = Path.Combine(modsRoot, StorageFolderPrefix + SafeFolderSegment(cleanPackageId));

            string legacy = Path.Combine(modsRoot, LegacyStorageModFolderName);
            if (!Directory.Exists(target) && Directory.Exists(legacy))
            {
                string legacyPackageId = ReadAboutPackageId(Path.Combine(legacy, "About", "About.xml"));
                if (string.Equals(NormalizePackageId(legacyPackageId), cleanPackageId, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Message("[CustomGeneExporter] 继续使用旧导出目录: " + legacy);
                    target = legacy;
                }
                else if (!string.IsNullOrEmpty(legacyPackageId))
                {
                    Log.Message("[CustomGeneExporter] 旧导出目录的 packageId 是 " + legacyPackageId
                        + "，与当前 " + cleanPackageId + " 不同，改用新目录: " + target);
                }
            }

            ResolvedStorageFolders[cleanPackageId] = target;
            return target;
        }

        private static string ReadAboutPackageId(string aboutPath)
        {
            try
            {
                if (!File.Exists(aboutPath))
                {
                    return null;
                }
                XDocument doc = XDocument.Load(aboutPath);
                XElement packageId = doc.Root == null ? null : doc.Root.Element("packageId");
                return packageId == null ? null : packageId.Value;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string GetStorageFolderPath(string packageId)
        {
            return ResolveStorageFolder(NormalizePackageId(packageId));
        }

        public static string GetGeneDefsFolderPath(string packageId)
        {
            return Path.Combine(GetStorageFolderPath(packageId), "Defs", "GeneDefs");
        }

        public static int CountExportedGeneFiles(string packageId)
        {
            try
            {
                string folder = GetGeneDefsFolderPath(packageId);
                return Directory.Exists(folder) ? Directory.GetFiles(folder, GeneFilePrefix + "*.xml").Length : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // 硬盘上已经导出的基因（文件名去掉前缀和扩展名）
        public static List<string> ListExportedGeneNames(string packageId)
        {
            List<string> result = new List<string>();
            try
            {
                string folder = GetGeneDefsFolderPath(packageId);
                if (!Directory.Exists(folder))
                {
                    return result;
                }
                string[] files = Directory.GetFiles(folder, GeneFilePrefix + "*.xml");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.StartsWith(GeneFilePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        name = name.Substring(GeneFilePrefix.Length);
                    }
                    result.Add(name);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[CustomGeneExporter] 读取已导出基因列表失败: " + ex.Message);
            }
            return result;
        }

        public static bool GeneFileExists(string packageId, string geneName)
        {
            try
            {
                return File.Exists(Path.Combine(GetGeneDefsFolderPath(packageId), GetGeneFileName(geneName)));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 按文件名前缀（不带 GegeXT_ 和 .xml）删除已经导出的基因
        public static bool DeleteExportedGeneFile(string packageId, string fileNameWithoutPrefix, out string error)
        {
            error = null;
            try
            {
                string folder = GetGeneDefsFolderPath(packageId);
                string path = Path.Combine(folder, GeneFilePrefix + fileNameWithoutPrefix + ".xml");
                if (!File.Exists(path))
                {
                    error = "CGET_DeleteFileNotFound".Translate(fileNameWithoutPrefix + ".xml");
                    return false;
                }
                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error("[CustomGeneExporter] 删除基因文件失败: " + ex);
                return false;
            }
        }

        #endregion

        //查defName冲突
        public static List<string> FindGeneNameConflicts(string modPackageId, List<GeneExportSpec> genes)
        {
            List<string> conflicts = new List<string>();
            if (genes == null)
            {
                return conflicts;
            }

            string defsFolder = GetGeneDefsFolderPath(modPackageId);
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

        // 同一个基因如果之前用别的 packageId 导出过，两个 Mod 会同时定义同一个 defName，游戏里会报重复。
        // 这里只做提醒，不阻止导出（返回 "文件夹 : 基因名"）。
        public static List<string> FindGenesInOtherExportFolders(string packageId, List<GeneExportSpec> genes)
        {
            List<string> result = new List<string>();
            if (genes == null || genes.Count == 0)
            {
                return result;
            }

            try
            {
                string currentFolder = Path.GetFullPath(GetStorageFolderPath(packageId));
                foreach (string folder in Directory.GetDirectories(GenFilePaths.ModsFolderPath))
                {
                    string folderName = Path.GetFileName(folder);
                    if (!string.Equals(folderName, LegacyStorageModFolderName, StringComparison.OrdinalIgnoreCase)
                        && !folderName.StartsWith(StorageFolderPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (string.Equals(Path.GetFullPath(folder), currentFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string defsFolder = Path.Combine(folder, "Defs", "GeneDefs");
                    if (!Directory.Exists(defsFolder))
                    {
                        continue;
                    }

                    foreach (GeneExportSpec gene in genes)
                    {
                        if (gene == null || string.IsNullOrEmpty(gene.Name))
                        {
                            continue;
                        }
                        if (File.Exists(Path.Combine(defsFolder, GetGeneFileName(gene.Name))))
                        {
                            result.Add(folderName + " : " + gene.Name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[CustomGeneExporter] 检查其它导出目录失败: " + ex.Message);
            }

            return result;
        }

        //批量导出
        public static bool ExportGenesToModFolder(
            string modName, string modAuthor, string modPackageId,
            List<GeneExportSpec> genes, bool overwriteExisting, string previewImagePath)
        {
            try
            {
                // 先校验 packageId，避免导出到一半才失败，也避免游戏启动时刷 packageId 格式警告
                string packageIdError = GetPackageIdError(modPackageId);
                if (packageIdError != null)
                {
                    Messages.Message(packageIdError, MessageTypeDefOf.RejectInput, false);
                    return false;
                }
                string cleanPackageId = NormalizePackageId(modPackageId);

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
                    return false;
                }

                // 名字里如果只有空格/标点，过滤后 defName 会变成空，必须拦下来
                List<string> invalidNames = new List<string>();
                foreach (GeneExportSpec gene in valid)
                {
                    if (!IsValidGeneName(gene.Name))
                    {
                        invalidNames.Add(gene.Name);
                    }
                }
                if (invalidNames.Count > 0)
                {
                    Messages.Message("CGET_GeneNameInvalid".Translate(string.Join(", ", invalidNames.ToArray())),
                        MessageTypeDefOf.RejectInput, false);
                    return false;
                }

                string storageFolder = GetStorageFolderPath(cleanPackageId);
                if (!EnsureWritable(storageFolder))
                {
                    Messages.Message("CGET_ExportNoPermission".Translate(GenFilePaths.ModsFolderPath), MessageTypeDefOf.ThreatBig, false);
                    Log.Error("[CustomGeneExporter] 没有写入权限: " + storageFolder);
                    return false;
                }

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
                    gene.ResolvedIconPath = null;

                    string imagePath = NormalizePath(gene.ImagePath);
                    if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                    {
                        string extension = Path.GetExtension(imagePath);
                        if (!IsSupportedIconExtension(extension))
                        {
                            // bmp/gif 之类游戏读不出来，会变成缺失贴图，直接提示并回落到默认图标
                            Log.Warning("[CustomGeneExporter] 图标格式不受支持(" + extension + ")，已改用默认图标: " + imagePath);
                            Messages.Message("CGET_IconFormatWarning".Translate(extension), MessageTypeDefOf.CautionInput, false);
                            extension = null;
                        }

                        if (extension != null)
                        {
                            // 每个基因单独命名，避免两个基因用同名图片(icon.png)时互相覆盖
                            string iconFileName = GetDefName(gene.Name) + extension;
                            File.Copy(imagePath, Path.Combine(texturesFolder, iconFileName), true);
                            gene.ResolvedIconPath = "CustomGenes/" + Path.GetFileNameWithoutExtension(iconFileName);
                        }
                    }

                    if (string.IsNullOrEmpty(gene.ResolvedIconPath))
                    {
                        if (gene.ColorType != GeneColorType.None)
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
                }

                //About.xml
                bool anyColorGene = valid.Any(g => g.ColorType != GeneColorType.None);
                List<ModContentPack> dependencyMods = CollectDependencyMods(valid);
                GenerateAboutFile(aboutFolder, modName, modAuthor, cleanPackageId, dependencyMods, anyColorGene);

                //自动生成的分类（defName 跟 packageId 绑定，label 用模组名，多个导出模组不会重名）
                string autoCategoryDefName = SafeFolderSegment(cleanPackageId.Replace('.', '_')) + "_Category";
                string autoCategoryLabel = string.IsNullOrEmpty(modName) ? (string)"CGET_DefaultCategory".Translate() : modName.Trim();
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
                                GenerateGeneCategoryFile(defsFolder, autoCategoryDefName, autoCategoryLabel);
                            }
                        }
                        else
                        {
                            categoryDefName = gene.Category.defName;
                        }

                        GenerateGeneDefFile(defsFolder, gene.Name, gene.Description ?? "", gene.Metabolism, gene.Complexity,
                            gene.DisablesNeeds, gene.StatFactors, gene.StatOffsets,
                            gene.ForcedTraits, gene.SuppressedTraits, gene.ResolvedIconPath,
                            gene.GeneClass, gene.GeneClassMod, categoryDefName, gene.Category, cleanPackageId);
                    }

                    written++;
                }

                string folderName = GetStorageFolderName(cleanPackageId);
                string message = skipped > 0
                    ? "CGET_ExportSuccessSkipped".Translate(written, folderName, skipped)
                    : "CGET_ExportSuccess".Translate(written, folderName);
                Messages.Message(message, MessageTypeDefOf.TaskCompletion, false);
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                Messages.Message("CGET_ExportNoPermission".Translate(GenFilePaths.ModsFolderPath), MessageTypeDefOf.ThreatBig, false);
                Log.Error("[CustomGeneExporter] " + ex);
                return false;
            }
            catch (Exception ex)
            {
                Messages.Message("CGET_ExportFailed".Translate(ex.Message), MessageTypeDefOf.ThreatBig, false);
                Log.Error("[CustomGeneExporter] " + ex.ToString());
                return false;
            }
        }

        // 游戏装在 Program Files 下时可能没有写权限，提前探测并给出明确提示
        private static bool EnsureWritable(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string probe = Path.Combine(folder, ".cget_write_probe");
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[CustomGeneExporter] 写入测试失败(" + folder + "): " + ex.Message);
                return false;
            }
        }

        private static bool IsSupportedIconExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension))
            {
                return false;
            }
            return SupportedIconExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
        }

        // 依赖 = 自定义 geneClass 的模组 + 用到的需求/属性所属的模组，缺一个都会让导出的基因变空壳
        private static List<ModContentPack> CollectDependencyMods(List<GeneExportSpec> genes)
        {
            List<ModContentPack> mods = new List<ModContentPack>();

            foreach (GeneExportSpec gene in genes)
            {
                if (gene == null)
                {
                    continue;
                }

                AddMod(mods, gene.GeneClassMod);

                if (gene.DisablesNeeds != null)
                {
                    foreach (NeedDef need in gene.DisablesNeeds)
                    {
                        if (need != null)
                        {
                            AddMod(mods, need.modContentPack);
                        }
                    }
                }
                if (gene.StatFactors != null)
                {
                    foreach (StatModifier factor in gene.StatFactors)
                    {
                        if (factor != null && factor.stat != null)
                        {
                            AddMod(mods, factor.stat.modContentPack);
                        }
                    }
                }
                if (gene.StatOffsets != null)
                {
                    foreach (StatModifier offset in gene.StatOffsets)
                    {
                        if (offset != null && offset.stat != null)
                        {
                            AddMod(mods, offset.stat.modContentPack);
                        }
                    }
                }
                if (gene.ForcedTraits != null)
                {
                    foreach (TraitDef trait in gene.ForcedTraits)
                    {
                        if (trait != null)
                        {
                            AddMod(mods, trait.modContentPack);
                        }
                    }
                }
                if (gene.SuppressedTraits != null)
                {
                    foreach (TraitDef trait in gene.SuppressedTraits)
                    {
                        if (trait != null)
                        {
                            AddMod(mods, trait.modContentPack);
                        }
                    }
                }
            }

            return mods;
        }

        private static void AddMod(List<ModContentPack> mods, ModContentPack mod)
        {
            if (mod == null || mod.IsCoreMod || string.IsNullOrEmpty(mod.PackageId))
            {
                return;
            }
            if (mods.Any(m => m != null && string.Equals(m.PackageId, mod.PackageId, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            mods.Add(mod);
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

        // 导出的基因 Mod 支持哪些版本：优先跟随本工具自己的 About.xml（工具支持 1.6/1.7，导出的基因也支持），
        // 读不到时退回 Core 的游戏版本。
        private static List<string> GetSupportedVersions()
        {
            List<string> result = new List<string>();

            try
            {
                ModContentPack self = LoadedModManager.RunningModsListForReading.FirstOrDefault(
                    m => m != null && string.Equals(m.PackageId, "yushiren.genesengineering", StringComparison.OrdinalIgnoreCase));
                if (self != null && self.ModMetaData != null && self.ModMetaData.SupportedVersionsReadOnly != null)
                {
                    foreach (System.Version version in self.ModMetaData.SupportedVersionsReadOnly)
                    {
                        AddVersionString(result, version);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[CustomGeneExporter] 读取本 Mod 支持版本失败: " + ex.Message);
            }

            if (result.Count == 0)
            {
                try
                {
                    ModContentPack core = LoadedModManager.RunningModsListForReading.FirstOrDefault(m => m != null && m.IsCoreMod);
                    if (core != null && core.ModMetaData != null
                        && core.ModMetaData.SupportedVersionsReadOnly != null
                        && core.ModMetaData.SupportedVersionsReadOnly.Count > 0)
                    {
                        foreach (System.Version version in core.ModMetaData.SupportedVersionsReadOnly)
                        {
                            AddVersionString(result, version);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[CustomGeneExporter] 读取游戏版本失败: " + ex.Message);
                }
            }

            if (result.Count == 0)
            {
                result.Add("1.6");
            }

            return result;
        }

        private static void AddVersionString(List<string> versions, System.Version version)
        {
            if (version == null)
            {
                return;
            }
            string text = version.Major + "." + version.Minor;
            if (!versions.Contains(text))
            {
                versions.Add(text);
            }
        }

        // 依赖项没有 URL 时，游戏每次启动都会刷 "needs to have <downloadUrl> and/or <steamWorkshopUrl>"
        private static string GetModDownloadUrl(ModContentPack mod)
        {
            try
            {
                if (mod == null)
                {
                    return null;
                }

                if (mod.IsOfficialMod && mod.SteamAppId > 0)
                {
                    return "https://store.steampowered.com/app/" + mod.SteamAppId + "/";
                }

                string rootDir = mod.RootDir;
                if (string.IsNullOrEmpty(rootDir))
                {
                    return null;
                }

                // ...\steamapps\workshop\content\294100\<publishedFileId>\...
                string[] parts = rootDir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length - 2; i++)
                {
                    if (!string.Equals(parts[i], "content", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    ulong publishedFileId;
                    if (ulong.TryParse(parts[i + 2], out publishedFileId) && publishedFileId > 0)
                    {
                        return "https://steamcommunity.com/sharedfiles/filedetails/?id=" + publishedFileId;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[CustomGeneExporter] 生成依赖链接失败: " + ex.Message);
            }
            return null;
        }

        private static void AddDependency(List<XElement> dependencies, List<XElement> loadAfter,
            HashSet<string> addedPackageIds, string packageId, string displayName, ModContentPack mod)
        {
            if (string.IsNullOrEmpty(packageId) || !addedPackageIds.Add(packageId))
            {
                return;
            }

            XElement dependency = new XElement("li",
                new XElement("packageId", packageId),
                new XElement("displayName", string.IsNullOrEmpty(displayName) ? packageId : displayName));

            string url = GetModDownloadUrl(mod);
            if (!string.IsNullOrEmpty(url))
            {
                dependency.Add(new XElement("downloadUrl", url));
            }

            dependencies.Add(dependency);
            loadAfter.Add(new XElement("li", packageId));
        }

        private static void GenerateAboutFile(string aboutFolder, string modName, string modAuthor, string modPackageId,
            List<ModContentPack> dependencyMods, bool requireBiotech)
        {
            string fileName = Path.Combine(aboutFolder, "About.xml");

            List<string> supportedVersions = GetSupportedVersions();
            XElement versionsElement = new XElement("supportedVersions");
            foreach (string version in supportedVersions)
            {
                versionsElement.Add(new XElement("li", version));
            }

            XElement modMetaData = new XElement("ModMetaData",
                new XElement("name", string.IsNullOrEmpty(modName) ? (string)"CGET_DefaultModName".Translate() : modName),
                new XElement("author", string.IsNullOrEmpty(modAuthor) ? (string)"CGET_DefaultModAuthor".Translate() : modAuthor),
                new XElement("packageId", modPackageId),
                versionsElement,
                new XElement("description", (string)"CGET_DefaultModDescription".Translate())
            );

            List<XElement> dependencies = new List<XElement>();
            List<XElement> loadAfter = new List<XElement>();
            HashSet<string> addedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (dependencyMods != null)
            {
                foreach (ModContentPack dependency in dependencyMods)
                {
                    if (dependency == null)
                    {
                        continue;
                    }
                    AddDependency(dependencies, loadAfter, addedPackageIds, dependency.PackageId, dependency.Name, dependency);
                }
            }

            //声明依赖
            const string biotechPackageId = "ludeon.rimworld.biotech";
            if (requireBiotech && !addedPackageIds.Contains(biotechPackageId))
            {
                ModContentPack biotech = LoadedModManager.RunningModsListForReading.FirstOrDefault(
                    m => m != null && string.Equals(m.PackageId, biotechPackageId, StringComparison.OrdinalIgnoreCase));
                AddDependency(dependencies, loadAfter, addedPackageIds, biotechPackageId,
                    biotech == null ? "Biotech" : biotech.Name, biotech);
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
            string fileName = Path.Combine(defsFolder, GetGeneFileName(name));

            XElement geneDef = new XElement("GeneDef",
                new XAttribute("ParentName", parentName),
                new XAttribute("MayRequire", "ludeon.rimworld.biotech"));

            geneDef.Add(
                new XElement("defName", GetDefName(name)),
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
    List<NeedDef> disablesNeeds, List<StatModifier> factors, List<StatModifier> offsets,
    List<TraitDef> forcedTraits, List<TraitDef> suppressedTraits, string iconPath,
    Type selectedGeneClass, ModContentPack selectedGeneClassMod,
            string categoryDefName, GeneCategoryDef selectedCategory, string ownPackageId)
        {
            string fileName = Path.Combine(defsFolder, GetGeneFileName(name));

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
                new XElement("defName", GetDefName(name)),
                new XElement("label", name),
                new XElement("description", desc)
            );

            if (!string.IsNullOrEmpty(iconPath))
            {
                geneDef.Add(new XElement("iconPath", iconPath));
            }

            geneDef.Add(
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
                        if (need == null)
                        {
                            continue;
                        }
                        XElement li = new XElement("li", need.defName);
                        if (need.modContentPack != null && !need.modContentPack.IsCoreMod)
                        {
                            li.Add(new XAttribute("MayRequire", need.modContentPack.PackageId));
                        }
                        disablesNeedsElement.Add(li);
                    }
                    if (disablesNeedsElement.HasElements)
                    {
                        geneDef.Add(disablesNeedsElement);
                    }
                }

                //强制获得 / 强制抑制特性
                AddGeneticTraitList(geneDef, "forcedTraits", forcedTraits);
                AddGeneticTraitList(geneDef, "suppressedTraits", suppressedTraits);

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

        // 特性列表：<forcedTraits><li><def>Kind</def></li></forcedTraits>
        // 注意 GeneDef 的这两个字段是 List<GeneticTraitData>，XML 里每项要写成 <li><def>特性defName</def></li>
        private static void AddGeneticTraitList(XElement geneDef, string elementName, List<TraitDef> traits)
        {
            if (traits == null || traits.Count == 0)
            {
                return;
            }

            XElement list = new XElement(elementName);
            foreach (TraitDef trait in traits)
            {
                if (trait == null || string.IsNullOrEmpty(trait.defName))
                {
                    continue;
                }

                XElement li = new XElement("li",
                    new XElement("def", trait.defName));

                if (trait.modContentPack != null && !trait.modContentPack.IsCoreMod)
                {
                    li.Add(new XAttribute("MayRequire", trait.modContentPack.PackageId));
                }

                list.Add(li);
            }

            if (list.HasElements)
            {
                geneDef.Add(list);
            }
        }
    }
}
