using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using System.IO;

namespace CustomGeneExporter
{
    public class Dialog_CreateCustomGene : Window
    {
        //基础数据
        private string geneName = "MyCustomGene";
        private string geneDescription = "This is a custom gene.";
        private int metabolism = 0;
        private int complexity = 0;
        private string iconPath = "";
        //基因分类
        private GeneCategoryDef selectedCategory = null;
        //基因类型：功能 / 皮肤（skinColorOverride）/ 发色（hairColorOverride）
        private GeneColorType colorGeneType = GeneColorType.None;
        //颜色 RGB（皮肤和发色共用）
        private string skinRedText = "200";
        private string skinGreenText = "160";
        private string skinBlueText = "120";
        //实验性调试模式
        private bool experimentalDebugMode = false;
        //列表数据
        private List<NeedDef> disablesNeedsList = new List<NeedDef>();
        private List<StatModifier> statFactors = new List<StatModifier>();//乘算
        private List<StatModifier> statOffsets = new List<StatModifier>(); //加算 

        // 属性数值输入框的文本缓冲（不然每帧回填会导致小数输不进去）
        private Dictionary<StatModifier, string> statValueBuffers = new Dictionary<StatModifier, string>();

        // 待导出到同一个 Mod 的基因列表（右侧表格显示的就是它）
        private List<GeneExportSpec> pendingGenes = new List<GeneExportSpec>();
        private Vector2 pendingGenesScroll = Vector2.zero;

        // 磁盘上已导出的基因数量（每秒最多扫一次目录）
        private int exportedGeneFileCount = 0;
        private float exportedGeneCountRefreshTime = -99f;

        //自定义 geneClass 数据
        private Type selectedGeneClass = null;
        private ModContentPack selectedGeneClassMod = null;

        //存放选中类的自定义效果描述
        private List<string> selectedClassCustomEffects = new List<string>();

        //导出的元数据
        private string modName = "My Custom Genes";
        private string modAuthor = "Player";
        private string modPackageId = "player.customgenes";

        //滚动
        private Vector2 leftScrollPos = Vector2.zero;
        private Vector2 rightScrollPos = Vector2.zero;

        //图片预览相关字段
        //初始化为null强制第一帧加载默认图
        private string lastLoadedIconPath = null;
        private Texture2D previewTexture = null; //当前要显示的图片
        private Texture2D defaultTexture = null; //默认图

        private bool IsValidPawnStat(StatDef stat)
        {
            //官方DLC
            if (stat.modContentPack == null || (!stat.modContentPack.IsCoreMod && !stat.modContentPack.IsOfficialMod))
                return false;

            //排除永远隐藏的属性
            if (stat.alwaysHide)
                return false;

            //原版 StatDef 里 showOnPawns=false 的属性不会出现在属性面板，
            if (!stat.showOnPawns)
                return false;

            //无意义特定属性
            if (stat.defName == "EMPResistance" || stat.defName == "Mass" || stat.defName == "MarketValue" || stat.defName == "Flammability")
                return false;

            //检查是否属于相关类别
            if (stat.category != null)
            {
                string catName = stat.category.defName;
                if (catName == "BasicsPawn" || catName.StartsWith("Pawn") || catName == "Mood")
                {
                    return true;
                }
            }

            //特殊补漏
            if (stat.defName == "GlobalWorkSpeed" || stat.defName == "MoveSpeed")
                return true;

            return false;
        }
        private static bool IsFactorStat(StatDef stat)
        {
            return Mathf.Approximately(stat.defaultBaseValue, 1f);
        }
        private bool IsValidNeed(NeedDef need)
        {
            // 官方 DLC
            if (need.modContentPack == null || (!need.modContentPack.IsCoreMod && !need.modContentPack.IsOfficialMod))
                return false;

            //过滤成瘾品需求
            if (need.defName.StartsWith("Chemical_") || need.needClass.Name.Contains("Chemical") || need.needClass.Name.Contains("Drug"))
                return false;

            //过滤特殊机制
            string[] excludeNames = {
                "KillThirst",
                "Authority",  
                "Deathrest", 
                "Bloodfeed",  
                "Hemogen",    
                "NeuralHeat", 
                "Psyfocus",
                "Learn", 
                "Suppression", 
                "Energy",        
                "Learning",   // Biotech 学习
                "MechEnergy", // Biotech 能量
                "Play"        // Biotech 玩乐
            };

            if (excludeNames.Contains(need.defName))
                return false;

            // 其他保留
            return true;
        }

        public override Vector2 InitialSize => new Vector2(900f, 700f);

        private Texture2D backgroundTexture = null;

        public Dialog_CreateCustomGene()
        {
            this.forcePause = true;
            this.doCloseX = true;
            this.closeOnClickedOutside = false;
            this.absorbInputAroundWindow = true;

            defaultTexture = ContentFinder<Texture2D>.Get("CustomGeneExporter/DefaultIcon", false) ?? BaseContent.BadTex;
            previewTexture = defaultTexture;

            backgroundTexture = ContentFinder<Texture2D>.Get("CustomGeneExporter/GeneBackground", false);

            //加载默认预览图
            defaultPreviewTexture = ContentFinder<Texture2D>.Get("CustomGeneExporter/DefaultPreview", false);
            if (defaultPreviewTexture == null) defaultPreviewTexture = defaultTexture;

            previewImageTexture = defaultPreviewTexture;
        }



        public override void DoWindowContents(Rect inRect)
        {
            // 检查并更新预览图片
            TryUpdatePreviewTexture();

            float halfWidth = (inRect.width - 20f) / 2f;
            Rect leftRect = new Rect(inRect.x, inRect.y, halfWidth, inRect.height);
            Rect rightRect = new Rect(inRect.x + halfWidth + 20f, inRect.y, halfWidth, inRect.height);

            DrawLeftInputArea(leftRect);
            DrawRightPreviewArea(rightRect);
        }
        // 处理路径变更时的图片加载逻辑
        private void TryUpdatePreviewTexture()
        {
            //Icon
            if (lastLoadedIconPath != iconPath)
            {
                lastLoadedIconPath = iconPath;
                if (previewTexture != null && previewTexture != defaultTexture && previewTexture != BaseContent.BadTex)
                {
                    UnityEngine.Object.Destroy(previewTexture);
                }
                previewTexture = LoadTextureFromPath(iconPath) ?? defaultTexture;
            }

            //Preview
            if (lastLoadedPreviewPath != previewImagePath)
            {
                lastLoadedPreviewPath = previewImagePath;
                if (previewImageTexture != null && previewImageTexture != defaultPreviewTexture && previewImageTexture != defaultTexture && previewImageTexture != BaseContent.BadTex)
                {
                    UnityEngine.Object.Destroy(previewImageTexture);
                }
                previewImageTexture = LoadTextureFromPath(previewImagePath) ?? defaultPreviewTexture;
            }
        }

        //从绝对路径加载
        private Texture2D LoadTextureFromPath(string path)
        {
            // 去掉资源管理器复制路径自带的一对双引号
            path = GeneExporter.NormalizePath(path);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                Texture2D tex = new Texture2D(2, 2);
                tex.LoadImage(data);
                return tex;
            }
            catch (Exception ex)
            {
                Log.Warning("[CustomGeneExporter] 无法加载图片: " + ex.Message);
                return null;
            }
        }
        //窗口关闭时清理内存
        public override void PostClose()
        {
            base.PostClose();
            if (previewTexture != null && previewTexture != defaultTexture && previewTexture != BaseContent.BadTex)
            {
                UnityEngine.Object.Destroy(previewTexture);
            }
            if (previewImageTexture != null && previewImageTexture != defaultPreviewTexture && previewImageTexture != defaultTexture && previewImageTexture != BaseContent.BadTex)
            {
                UnityEngine.Object.Destroy(previewImageTexture);
            }
        }

        private float cachedContentHeight = 700f;

        private void DrawLeftInputArea(Rect rect)
        {
            //创建滚动
            Rect viewRect = new Rect(0f, 0f, rect.width - 16f, cachedContentHeight);

            Widgets.BeginScrollView(rect, ref leftScrollPos, viewRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(new Rect(0f, 0f, viewRect.width, 9999f));
            DrawLeftContent(listing);
            cachedContentHeight = Mathf.Max(listing.CurHeight + 20f, rect.height);

            listing.End();

            Widgets.EndScrollView();
        }


        private void DrawLeftContent(Listing_Standard listing)
        {
            //导出 Mod 元数据
            listing.Label("<b>" + "CGET_ExportModInfo".Translate() + "</b>");

            listing.Label("CGET_ModNameLabel".Translate());
            modName = listing.TextEntry(modName);

            listing.Label("CGET_ModAuthorLabel".Translate());
            modAuthor = listing.TextEntry(modAuthor);

            listing.Label("CGET_ModPackageIdLabel".Translate());
            modPackageId = listing.TextEntry(modPackageId);

            listing.GapLine();//////

            // 定义新颜色
            Color highlightColor = new Color32(230, 212, 4, 255); // 对应 #e6d404

            listing.Label("CGET_GeneDefNameLabel".Translate());
            geneName = listing.TextEntry(geneName);

            listing.Label("CGET_GeneDescriptionLabel".Translate());
            geneDescription = listing.TextEntry(geneDescription, 3);

            listing.GapLine();

            // 基因类型选择
            listing.Label("CGET_GeneTypeLabel".Translate());
            string geneTypeLabel = colorGeneType == GeneColorType.SkinColor ? "CGET_GeneTypeSkin".Translate()
                : colorGeneType == GeneColorType.HairColor ? "CGET_GeneTypeHair".Translate()
                : "CGET_GeneTypeFunctional".Translate();
            if (listing.ButtonText(geneTypeLabel))
            {
                List<FloatMenuOption> typeOptions = new List<FloatMenuOption>();
                typeOptions.Add(new FloatMenuOption("CGET_GeneTypeFunctional".Translate(), delegate
                {
                    colorGeneType = GeneColorType.None;
                }));
                typeOptions.Add(new FloatMenuOption("CGET_GeneTypeSkin".Translate(), delegate
                {
                    colorGeneType = GeneColorType.SkinColor;
                    ClearGeneClassForColorGene();
                }));
                typeOptions.Add(new FloatMenuOption("CGET_GeneTypeHair".Translate(), delegate
                {
                    colorGeneType = GeneColorType.HairColor;
                    ClearGeneClassForColorGene();
                }));
                Find.WindowStack.Add(new FloatMenu(typeOptions));
            }

            listing.GapLine();

            listing.Label("CGET_MetabolismLine".Translate(metabolism));
            metabolism = (int)listing.Slider(metabolism, -10f, 10f);

            listing.Label("CGET_ComplexityLine".Translate(complexity));
            complexity = (int)listing.Slider(complexity, 0f, 10f);

            listing.GapLine();

            //分类选择
            string defaultCategoryLabel = colorGeneType == GeneColorType.SkinColor ? "CGET_SkinCategoryDefault".Translate()
                : colorGeneType == GeneColorType.HairColor ? "CGET_HairCategoryDefault".Translate()
                : "CGET_CategoryDefault".Translate((string)"CGET_DefaultCategory".Translate());
            string categoryLabel = selectedCategory == null ? defaultCategoryLabel : selectedCategory.label;
            listing.Label("CGET_CategoryLabel".Translate());
            if (listing.ButtonText(categoryLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();

                //默认选项
                options.Add(new FloatMenuOption(defaultCategoryLabel, delegate {
                    selectedCategory = null;
                }));

                //遍历所有原版分类
                foreach (var cat in DefDatabase<GeneCategoryDef>.AllDefsListForReading.OrderBy(x => x.label))
                {
                    options.Add(new FloatMenuOption(cat.label, delegate {
                        selectedCategory = cat;
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            listing.GapLine();

            // 皮肤 / 发色基因：只需要颜色，属性、禁用需求、自定义 class 全部隐藏
            if (colorGeneType != GeneColorType.None)
            {
                DrawColorGeneFields(listing);
            }
            else
            {
            //自定义 geneClass 选择
            GUI.color = highlightColor;
            listing.Label("CGET_ExperimentalLabel".Translate());
            GUI.color = Color.white;//恢复颜色
            string classLabel = selectedGeneClass == null
                ? "CGET_SelectGeneClass".Translate()
                : "CGET_GeneClassFrom".Translate(selectedGeneClass.Name, selectedGeneClassMod?.Name);

            //自定义 GeneClass选择
            if (listing.ButtonText(classLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                options.Add(new FloatMenuOption("CGET_Default".Translate(), delegate {
                    selectedGeneClass = null;
                    selectedGeneClassMod = null;
                    selectedClassCustomEffects.Clear();
                }));

                var customClasses = GetCustomGeneClasses();
                foreach (var item in customClasses)
                {
                    options.Add(new FloatMenuOption("CGET_GeneClassFrom".Translate(item.Type.Name, item.Mod.Name), delegate {
                        selectedGeneClass = item.Type;
                        selectedGeneClassMod = item.Mod;

                        //清空之前选择的
                        disablesNeedsList.Clear();
                        statFactors.Clear();
                        statOffsets.Clear();

                        selectedClassCustomEffects.Clear();
                        var foundDef = DefDatabase<GeneDef>.AllDefsListForReading.FirstOrDefault(g => g.geneClass == item.Type);
                        if (foundDef != null && foundDef.customEffectDescriptions != null)
                        {
                            selectedClassCustomEffects.AddRange(foundDef.customEffectDescriptions);
                        }
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            //判断是否选择了自定义类
            if (selectedGeneClass != null)
            {
                //选择了自定义类时，提示
                GUI.color = highlightColor;
                listing.Label("CGET_CustomClassWarning1".Translate());
                listing.Label("CGET_CustomClassWarning2".Translate());
                GUI.color = Color.white; // 恢复颜色
            }
            else
            {
                listing.GapLine();
                // 实验性调试开关
                GUI.color = highlightColor;
                listing.CheckboxLabeled("CGET_ExperimentalDebugLabel".Translate(), ref experimentalDebugMode, "CGET_ExperimentalDebugTip".Translate());
                GUI.color = Color.white; // 恢复颜色
                //没有选择自定义类时
                listing.Label("CGET_DisableNeedsLabel".Translate());
                if (listing.ButtonText("CGET_Add".Translate()))
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    // 实验性模式下不过滤，否则使用 IsValidNeed
                    var validNeeds = experimentalDebugMode
                        ? DefDatabase<NeedDef>.AllDefsListForReading
                        : DefDatabase<NeedDef>.AllDefsListForReading.Where(IsValidNeed);

                    foreach (NeedDef needDef in validNeeds.OrderBy(x => x.label))
                    {
                        options.Add(new FloatMenuOption(needDef.label, delegate
                        {
                            // 检查是否已经添加过
                            if (!disablesNeedsList.Contains(needDef))
                            {
                                disablesNeedsList.Add(needDef);
                            }
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }

                for (int i = 0; i < disablesNeedsList.Count; i++)
                {
                    NeedDef need = disablesNeedsList[i];
                    Rect rowRect = listing.GetRect(30f);
                    Widgets.Label(new Rect(rowRect.x, rowRect.y, 200f, 30f), need.label);
                    //对齐
                    if (Widgets.ButtonText(new Rect(rowRect.x + 320f, rowRect.y, 60f, 28f), "CGET_Delete".Translate()))
                    {
                        disablesNeedsList.RemoveAt(i);
                        i--;
                    }
                }

                listing.GapLine();
                //StatFactors列表（只有倍率型属性：defaultBaseValue = 1）
                listing.Label("CGET_StatFactorsLabel".Translate());
                if (listing.ButtonText("CGET_Add".Translate()))
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    var factorStats = GetSelectableStatDefs().Where(IsFactorStat);

                    foreach (StatDef statDef in factorStats.OrderBy(x => x.label))
                    {
                        options.Add(new FloatMenuOption(statDef.label, delegate
                        {
                            // 检查是否已经添加过，加入到 statFactors (乘算)
                            if (!statFactors.Any(x => x.stat == statDef))
                            {
                                statFactors.Add(new StatModifier { stat = statDef, value = 1f }); // 乘算默认值 1
                            }
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }

                // 渲染乘算列表
                for (int i = 0; i < statFactors.Count; i++)
                {
                    StatModifier modifier = statFactors[i];
                    Rect rowRect = listing.GetRect(30f);
                    Widgets.Label(new Rect(rowRect.x, rowRect.y, 200f, 30f), modifier.stat.label);

                    DrawStatValueField(new Rect(rowRect.x + 210f, rowRect.y, 100f, 28f), modifier);

                    if (Widgets.ButtonText(new Rect(rowRect.x + 320f, rowRect.y, 60f, 28f), "CGET_Delete".Translate()))
                    {
                        statValueBuffers.Remove(modifier);
                        statFactors.RemoveAt(i);
                        i--;
                    }
                }

                listing.GapLine();

                //StatOffsets（数值型属性：defaultBaseValue != 1）
                listing.Label("CGET_StatOffsetsLabel".Translate());
                if (listing.ButtonText("CGET_Add".Translate()))
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    var offsetStats = GetSelectableStatDefs().Where(s => !IsFactorStat(s));

                    foreach (StatDef statDef in offsetStats.OrderBy(x => x.label))
                    {
                        options.Add(new FloatMenuOption(statDef.label, delegate
                        {
                            // 检查是否已经添加过，加入到 statOffsets (加算)
                            if (!statOffsets.Any(x => x.stat == statDef))
                            {
                                statOffsets.Add(new StatModifier { stat = statDef, value = 1f }); // 加算默认值 1（整数）
                            }
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }

                // 渲染加算列表
                for (int i = 0; i < statOffsets.Count; i++)
                {
                    StatModifier modifier = statOffsets[i];
                    Rect rowRect = listing.GetRect(30f);
                    Widgets.Label(new Rect(rowRect.x, rowRect.y, 200f, 30f), modifier.stat.label);

                    DrawStatValueField(new Rect(rowRect.x + 210f, rowRect.y, 100f, 28f), modifier);

                    if (Widgets.ButtonText(new Rect(rowRect.x + 320f, rowRect.y, 60f, 28f), "CGET_Delete".Translate()))
                    {
                        statValueBuffers.Remove(modifier);
                        statOffsets.RemoveAt(i);
                        i--;
                    }
                }
            }
            }

            listing.GapLine();///////
            listing.Label("CGET_IconPathLabel".Translate());
            iconPath = listing.TextEntry(iconPath);
            listing.Label("CGET_PreviewImagePathLabel".Translate()); //
            previewImagePath = listing.TextEntry(previewImagePath); //
            listing.GapLine();//////

            // 把当前编辑内容存进待导出列表（同一个 Mod 可以放多个基因）
            if (listing.ButtonText("CGET_AddToModList".Translate(pendingGenes.Count)))
            {
                AddCurrentGeneToList();
            }

            if (listing.ButtonText("CGET_ExportButton".Translate()))
            {
                // 导出前校验packageId
                if (string.IsNullOrEmpty(modPackageId))
                {
                    Messages.Message("CGET_PackageIdRequired".Translate(), MessageTypeDefOf.RejectInput, false);
                    return;
                }

                TryExportAll();
            }

            listing.Gap(20f);
        }

        // 当前编辑区 -> 待导出条目
        private GeneExportSpec CaptureCurrentGene()
        {
            GeneExportSpec spec = new GeneExportSpec();
            spec.Name = geneName == null ? "" : geneName.Trim();
            spec.Description = geneDescription ?? "";
            spec.Metabolism = metabolism;
            spec.Complexity = complexity;
            spec.ColorType = colorGeneType;
            spec.ColorR = ParseRgbChannel(skinRedText, 200);
            spec.ColorG = ParseRgbChannel(skinGreenText, 160);
            spec.ColorB = ParseRgbChannel(skinBlueText, 120);
            spec.ImagePath = iconPath ?? "";
            spec.Category = selectedCategory;
            spec.GeneClass = selectedGeneClass;
            spec.GeneClassMod = selectedGeneClassMod;

            spec.DisablesNeeds.AddRange(disablesNeedsList);
            foreach (StatModifier factor in statFactors)
            {
                if (factor != null && factor.stat != null)
                {
                    spec.StatFactors.Add(new StatModifier { stat = factor.stat, value = factor.value });
                }
            }
            foreach (StatModifier offset in statOffsets)
            {
                if (offset != null && offset.stat != null)
                {
                    spec.StatOffsets.Add(new StatModifier { stat = offset.stat, value = offset.value });
                }
            }

            return spec;
        }

        // 待导出条目 -> 编辑区
        private void LoadGeneFromList(GeneExportSpec spec)
        {
            if (spec == null)
            {
                return;
            }

            geneName = spec.Name;
            geneDescription = spec.Description;
            metabolism = spec.Metabolism;
            complexity = spec.Complexity;
            colorGeneType = spec.ColorType;
            skinRedText = spec.ColorR.ToString();
            skinGreenText = spec.ColorG.ToString();
            skinBlueText = spec.ColorB.ToString();
            iconPath = spec.ImagePath ?? "";
            selectedCategory = spec.Category;
            selectedGeneClass = spec.GeneClass;
            selectedGeneClassMod = spec.GeneClassMod;

            disablesNeedsList.Clear();
            disablesNeedsList.AddRange(spec.DisablesNeeds);

            statFactors.Clear();
            foreach (StatModifier factor in spec.StatFactors)
            {
                statFactors.Add(new StatModifier { stat = factor.stat, value = factor.value });
            }

            statOffsets.Clear();
            foreach (StatModifier offset in spec.StatOffsets)
            {
                statOffsets.Add(new StatModifier { stat = offset.stat, value = offset.value });
            }
            statValueBuffers.Clear();
            selectedClassCustomEffects.Clear();
        }

        private void AddCurrentGeneToList()
        {
            GeneExportSpec spec = CaptureCurrentGene();
            if (spec.Name.Length == 0)
            {
                Messages.Message("CGET_GeneNameRequired".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            int existingIndex = pendingGenes.FindIndex(g => string.Equals(g.Name, spec.Name, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                pendingGenes[existingIndex] = spec;
                Messages.Message("CGET_GeneUpdated".Translate(spec.Name), MessageTypeDefOf.TaskCompletion, false);
            }
            else
            {
                pendingGenes.Add(spec);
                Messages.Message("CGET_GeneAdded".Translate(spec.Name, pendingGenes.Count), MessageTypeDefOf.TaskCompletion, false);
            }
        }

        private void TryExportAll()
        {
            List<GeneExportSpec> genes;
            if (pendingGenes.Count > 0)
            {
                genes = new List<GeneExportSpec>(pendingGenes);

                string editingName = geneName == null ? "" : geneName.Trim();
                bool editingInList = editingName.Length == 0
                    || pendingGenes.Any(g => string.Equals(g.Name, editingName, StringComparison.OrdinalIgnoreCase));
                if (!editingInList)
                {
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "CGET_UnsavedGeneConfirm".Translate(editingName, genes.Count),
                        delegate { StartExport(genes); }, false, "CGET_Notice".Translate()));
                    return;
                }
            }
            else
            {
                GeneExportSpec single = CaptureCurrentGene();
                if (single.Name.Length == 0)
                {
                    Messages.Message("CGET_GeneNameRequired".Translate(), MessageTypeDefOf.ThreatBig, false);
                    return;
                }
                genes = new List<GeneExportSpec> { single };
            }

            StartExport(genes);
        }

        // 真正导出：先查 defName 重复，重复就弹窗确认覆盖
        private void StartExport(List<GeneExportSpec> genes)
        {
            List<string> conflicts = GeneExporter.FindGeneNameConflicts(modPackageId, genes);
            if (conflicts.Count > 0)
            {
                string conflictList = string.Join("\n", conflicts.Select(c => "• " + c).ToArray());
                string text = "CGET_DefNameConflict".Translate(conflictList);
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, delegate
                {
                    exportedGeneCountRefreshTime = -99f;
                    GeneExporter.ExportGenesToModFolder(modName, modAuthor, modPackageId, genes, true, previewImagePath);
                }, true, "CGET_DefNameConflictTitle".Translate()));
                return;
            }

            exportedGeneCountRefreshTime = -99f;
            GeneExporter.ExportGenesToModFolder(modName, modAuthor, modPackageId, genes, false, previewImagePath);
        }

        private List<CustomClassInfo> GetCustomGeneClasses()
        {
            var result = new List<CustomClassInfo>();
            var seenTypes = new HashSet<Type>();

            foreach (var def in DefDatabase<GeneDef>.AllDefsListForReading)
            {
                if (def.geneClass != typeof(Gene) && !seenTypes.Contains(def.geneClass))
                {
                    seenTypes.Add(def.geneClass);
                    result.Add(new CustomClassInfo
                    {
                        Type = def.geneClass,
                        Mod = def.modContentPack
                    });
                }
            }
            return result.OrderBy(x => x.Mod.Name).ThenBy(x => x.Type.Name).ToList();
        }

        private class CustomClassInfo
        {
            public Type Type;
            public ModContentPack Mod;
        }

        private void DrawRightPreviewArea(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            Rect innerRect = rect.ContractedBy(10f);

            //Preview
            float previewAreaHeight = 120f;
            Rect previewBgRect = new Rect(innerRect.x, innerRect.yMax - previewAreaHeight, innerRect.width, previewAreaHeight);
            Widgets.DrawBoxSolid(previewBgRect, new Color(0.1f, 0.1f, 0.1f, 0.5f));

            // 已添加基因表格
            const float tableHeight = 164f;
            const float tableGap = 6f;
            Rect tableRect = new Rect(innerRect.x, previewBgRect.y - tableHeight - tableGap,
                innerRect.width, tableHeight);

            // 皮肤基因：HSV 色轮 
            const float paletteHeight = 140f;
            Rect paletteRect = new Rect(innerRect.x, tableRect.y - paletteHeight - tableGap,
                innerRect.width, paletteHeight);

            if (previewImageTexture != null)
            {
                GUI.DrawTexture(previewBgRect.ContractedBy(10f), previewImageTexture, ScaleMode.ScaleToFit);
            }

            if (previewImageTexture == defaultPreviewTexture)
            {
                Rect labelRect = new Rect(previewBgRect.x, previewBgRect.yMax - 30f, previewBgRect.width, 24f);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(labelRect, "CGET_DefaultPreviewInUse".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
            }

            //Icon
            float imgSize = 128f;
            Rect imgRect = new Rect(innerRect.xMax - imgSize, innerRect.y + 10f, imgSize, imgSize);
            Widgets.DrawBoxSolid(imgRect, new Color(0.1f, 0.1f, 0.1f, 0.5f));

            if (backgroundTexture != null)
            {
                GUI.DrawTexture(imgRect, backgroundTexture, ScaleMode.ScaleToFit);
            }

            float iconSize = imgSize * 0.7f;
            Rect iconInnerRect = new Rect(imgRect.x + (imgSize - iconSize) / 2f, imgRect.y + (imgSize - iconSize) / 2f, iconSize, iconSize);

            // 皮肤
            bool usingInheritedColorIcon = false;
            if (colorGeneType != GeneColorType.None && previewTexture == defaultTexture)
            {
                Texture2D colorIcon = GetColorGeneIconTexture(colorGeneType);
                if (colorIcon != null)
                {
                    usingInheritedColorIcon = true;

                    int r = ParseRgbChannel(skinRedText, 200);
                    int g = ParseRgbChannel(skinGreenText, 160);
                    int b = ParseRgbChannel(skinBlueText, 120);

                    Color oldColor = GUI.color;
                    GUI.color = new Color(r / 255f, g / 255f, b / 255f, 1f);
                    GUI.DrawTexture(imgRect, colorIcon, ScaleMode.ScaleToFit);
                    GUI.color = oldColor;
                }
            }

            if (!usingInheritedColorIcon && previewTexture != null)
            {
                GUI.DrawTexture(iconInnerRect, previewTexture, ScaleMode.ScaleToFit);
            }

            if (usingInheritedColorIcon || previewTexture == defaultTexture)
            {
                string iconLabel = !usingInheritedColorIcon ? "CGET_DefaultIcon".Translate()
                    : colorGeneType == GeneColorType.HairColor ? "CGET_VanillaHairIcon".Translate() : "CGET_VanillaSkinIcon".Translate();
                Rect labelRect = new Rect(imgRect.x - 20f, imgRect.yMax + 5f, imgRect.width + 40f, 30f);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(labelRect, iconLabel);
                Text.Anchor = TextAnchor.UpperLeft;
            }

            // 已添加基因表格 + 颜色调色板
            DrawPendingGenesTable(tableRect);

            if (colorGeneType != GeneColorType.None)
            {
                DrawColorGenePalette(paletteRect);
            }

            // 左侧文本区域
            // 文本高度 = 整个高度 - 预览图 - 表格 - 调色板 - 间距
            float bottomReserved = previewAreaHeight + tableGap + tableHeight
                + (colorGeneType != GeneColorType.None ? tableGap + paletteHeight : 0f);
            float textAreaHeight = Mathf.Max(innerRect.height - bottomReserved - 10f, 80f);
            // 文本宽度 = 总宽度 - 图片宽度 - 间距
            float textWidth = innerRect.width - imgSize - 20f;
            Rect textOutRect = new Rect(innerRect.x, innerRect.y, textWidth, textAreaHeight);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("CGET_ExportedModInfoHeader".Translate());
            sb.AppendLine("CGET_LineName".Translate(modName));
            sb.AppendLine("CGET_LineAuthor".Translate(modAuthor));
            sb.AppendLine("CGET_LinePackageId".Translate(modPackageId));
            sb.AppendLine();

            sb.AppendLine("CGET_GeneInfoPreviewHeader".Translate());
            sb.AppendLine("CGET_LineName".Translate(string.IsNullOrEmpty(geneName) ? (string)"CGET_Unnamed".Translate() : geneName));
            sb.AppendLine("CGET_LineDescription".Translate(string.IsNullOrEmpty(geneDescription) ? (string)"CGET_NoDescription".Translate() : geneDescription));
            sb.AppendLine("CGET_MetabolismLine".Translate(metabolism));
            sb.AppendLine("CGET_ComplexityLine".Translate(complexity));
            sb.AppendLine("CGET_LineCategory".Translate(selectedCategory == null
                ? (colorGeneType == GeneColorType.SkinColor ? (string)"CGET_CategorySkin".Translate()
                    : colorGeneType == GeneColorType.HairColor ? (string)"CGET_CategoryHair".Translate()
                    : (string)"CGET_DefaultCategory".Translate())
                : selectedCategory.label));
            sb.AppendLine("CGET_LineGeneType".Translate(GetColorGeneTypeLabel()));

            if (colorGeneType != GeneColorType.None)
            {
                int r = ParseRgbChannel(skinRedText, 200);
                int g = ParseRgbChannel(skinGreenText, 160);
                int b = ParseRgbChannel(skinBlueText, 120);
                string hex = GetSkinColorHex();
                string colorLabel = colorGeneType == GeneColorType.HairColor ? "CGET_HairColor".Translate() : "CGET_SkinColor".Translate();
                sb.AppendLine("CGET_ColorPreviewLine".Translate(colorLabel, hex, r, g, b));
                sb.AppendLine("CGET_OutputResult".Translate(GeneExporter.FormatColor(r, g, b)));
            }

            if (selectedGeneClass != null)
            {
                sb.AppendLine("CGET_LineGeneClass".Translate(selectedGeneClass.FullName));
                sb.AppendLine("CGET_LineSourceMod".Translate(selectedGeneClassMod.Name));

                if (selectedClassCustomEffects.Count > 0)
                {
                    sb.AppendLine("CGET_CustomEffectsHeader".Translate());
                    foreach (var effect in selectedClassCustomEffects)
                    {
                        sb.AppendLine("  - " + effect);
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("CGET_EffectsHeader".Translate());

            bool hasListEffects = false;
            if (colorGeneType == GeneColorType.None)
            {
                if (disablesNeedsList.Count > 0)
                {
                    hasListEffects = true;
                    foreach (var need in disablesNeedsList)
                    {
                        sb.AppendLine("CGET_RemovesNeed".Translate(need.label));
                    }
                }

                if (statFactors.Count > 0)
                {
                    hasListEffects = true;
                    foreach (var factor in statFactors)
                    {
                        sb.AppendLine("• " + factor.stat.label + " " + DescribeStatModifier(factor, true));
                    }
                }

                if (statOffsets.Count > 0)
                {
                    hasListEffects = true;
                    foreach (var offset in statOffsets)
                    {
                        sb.AppendLine("• " + offset.stat.label + " " + DescribeStatModifier(offset, false));
                    }
                }
            }
            else
            {
                hasListEffects = true;
                sb.AppendLine("CGET_ColorCarrierLine".Translate(GetSkinColorHex()));
            }

            if (!hasListEffects)
            {
                sb.AppendLine("CGET_NoEffects".Translate());
            }

            //计算并渲染滚动文本
            float textHeight = Text.CalcHeight(sb.ToString(), textWidth - 16f);
            Rect textViewRect = new Rect(0f, 0f, textWidth - 16f, textHeight + 10f);

            Widgets.BeginScrollView(textOutRect, ref rightScrollPos, textViewRect);
            Widgets.Label(new Rect(0f, 0f, textViewRect.width, textHeight), sb.ToString());
            Widgets.EndScrollView();
        }
        //影响栏
        private static string DescribeStatModifier(StatModifier modifier, bool isFactor)
        {
            if (modifier == null || modifier.stat == null)
            {
                return "-";
            }

            string number = FormatStatNumber(modifier.value);
            if (isFactor)
            {
                return "×" + number;
            }

            string signed = modifier.value >= 0f ? "+" + number : number;

            //百分比类属性
            if (IsPercentToStringStyle(modifier.stat))
            {
                string percent = (modifier.value * 100f).ToString(GetPercentFormat(modifier.stat), CultureInfo.InvariantCulture);
                return "CGET_OffsetWithPercent".Translate(signed, percent);
            }

            return signed;
        }

        // PercentZero / PercentOne / PercentTwo 这三种样式在游戏里是“值 × 100 + %”
        private static bool IsPercentToStringStyle(StatDef stat)
        {
            return stat.toStringStyle == ToStringStyle.PercentZero
                || stat.toStringStyle == ToStringStyle.PercentOne
                || stat.toStringStyle == ToStringStyle.PercentTwo;
        }

        private static string GetPercentFormat(StatDef stat)
        {
            switch (stat.toStringStyle)
            {
                case ToStringStyle.PercentZero:
                    return "+0;-0;0";
                case ToStringStyle.PercentOne:
                    return "+0.#;-0.#;0";
                default:
                    return "+0.##;-0.##;0";
            }
        }

        //数值统一格式
        private static string FormatStatNumber(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        //下拉候选
        private IEnumerable<StatDef> GetSelectableStatDefs()
        {
            return experimentalDebugMode
                ? DefDatabase<StatDef>.AllDefsListForReading
                : DefDatabase<StatDef>.AllDefsListForReading.Where(IsValidPawnStat);
        }

        // 属性数值输入框
        private void DrawStatValueField(Rect rect, StatModifier modifier)
        {
            string buffer;
            if (!statValueBuffers.TryGetValue(modifier, out buffer))
            {
                buffer = FormatStatNumber(modifier.value);
            }

            string edited = Widgets.TextField(rect, buffer);
            if (edited != buffer)
            {
                statValueBuffers[modifier] = edited;

                string trimmed = (edited ?? "").Trim();
                if (trimmed.Length > 0)
                {
                    float parsed;
                    if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                        || float.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
                    {
                        modifier.value = parsed;
                    }
                }
            }
        }

        //颜色输入
        private void DrawColorGeneFields(Listing_Standard listing)
        {
            string colorLabel = colorGeneType == GeneColorType.HairColor ? "CGET_HairColor".Translate() : "CGET_SkinColor".Translate();
            string elementName = colorGeneType == GeneColorType.HairColor ? "hairColorOverride" : "skinColorOverride";

            listing.Label("CGET_ColorOverride".Translate(colorLabel, elementName));
            listing.Label("CGET_EnterRgb".Translate());

            Rect rowRect = listing.GetRect(28f);
            float fieldWidth = (rowRect.width - 16f) / 3f;
            skinRedText = Widgets.TextField(new Rect(rowRect.x, rowRect.y, fieldWidth, 26f), skinRedText);
            skinGreenText = Widgets.TextField(new Rect(rowRect.x + fieldWidth + 8f, rowRect.y, fieldWidth, 26f), skinGreenText);
            skinBlueText = Widgets.TextField(new Rect(rowRect.x + (fieldWidth + 8f) * 2f, rowRect.y, fieldWidth, 26f), skinBlueText);

            listing.Gap(6f);

            int r = ParseRgbChannel(skinRedText, 200);
            int g = ParseRgbChannel(skinGreenText, 160);
            int b = ParseRgbChannel(skinBlueText, 120);

            // 色块预览
            Rect colorRect = listing.GetRect(28f);
            Widgets.DrawBoxSolid(colorRect, new Color32((byte)r, (byte)g, (byte)b, 255));
            Widgets.DrawBox(colorRect);

            listing.Gap(4f);
            listing.Label(string.Format("R {0}   G {1}   B {2}   HEX #{3}", r, g, b, GetSkinColorHex()));
            listing.Label("CGET_GeneratedXml".Translate(elementName, GeneExporter.FormatColor(r, g, b)));
        }

        private string GetSkinColorHex()
        {
            int r = ParseRgbChannel(skinRedText, 200);
            int g = ParseRgbChannel(skinGreenText, 160);
            int b = ParseRgbChannel(skinBlueText, 120);
            return r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
        }

        //颜色基因图标
        private Texture2D GetColorGeneIconTexture(GeneColorType type)
        {
            if (type == GeneColorType.HairColor)
            {
                if (hairColorIconTexture == null && !triedLoadHairColorIcon)
                {
                    triedLoadHairColorIcon = true;
                    hairColorIconTexture = ContentFinder<Texture2D>.Get("UI/Icons/Genes/Gene_HairColor", false);
                    if (hairColorIconTexture == null)
                    {
                        Log.Warning("[CustomGeneExporter] 未能加载原版发色图标 UI/Icons/Genes/Gene_HairColor");
                    }
                }
                return hairColorIconTexture;
            }

            if (skinColorIconTexture == null && !triedLoadSkinColorIcon)
            {
                triedLoadSkinColorIcon = true;
                skinColorIconTexture = ContentFinder<Texture2D>.Get("UI/Icons/Genes/Gene_SkinColorOverride", false);
                if (skinColorIconTexture == null)
                {
                    Log.Warning("[CustomGeneExporter] 未能加载原版肤色图标 UI/Icons/Genes/Gene_SkinColorOverride");
                }
            }
            return skinColorIconTexture;
        }

        // 右侧调色板：HSV 色轮
        private void DrawPendingGenesTable(Rect rect)
        {
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 24f),
                "CGET_PendingGenesHeader".Translate(pendingGenes.Count, GetExportedGeneFileCount()));

            const float rowHeight = 28f;
            const float typeColumnWidth = 64f;
            const float deleteColumnWidth = 30f;
            const float columnGap = 10f;

            float contentWidth = Mathf.Max(rect.width - 16f, 140f);
            float nameColumnWidth = contentWidth - typeColumnWidth - deleteColumnWidth - columnGap * 2f;
            float typeColumnX = nameColumnWidth + columnGap;
            float deleteColumnX = typeColumnX + typeColumnWidth + columnGap;

            // 表头
            float headerY = rect.y + 34f;
            Widgets.Label(new Rect(rect.x + 8f, headerY, nameColumnWidth, 24f), "<i>DefName</i>");
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(rect.x + typeColumnX, headerY, typeColumnWidth, 24f), "CGET_TypeColumn".Translate());
            Text.Anchor = TextAnchor.UpperLeft;

            float bodyTop = headerY + 30f;
            Rect body = new Rect(rect.x, bodyTop, rect.width, Mathf.Max(rect.yMax - bodyTop, 26f));

            if (pendingGenes.Count == 0)
            {
                Widgets.Label(new Rect(body.x + 4f, body.y, body.width, 24f),
                    "<color=#909090>" + "CGET_NoGenesAdded".Translate() + "</color>");
                return;
            }

            Rect viewRect = new Rect(0f, 0f, contentWidth, Mathf.Max(pendingGenes.Count * rowHeight, rowHeight));

            Widgets.BeginScrollView(body, ref pendingGenesScroll, viewRect);
            for (int i = 0; i < pendingGenes.Count; i++)
            {
                GeneExportSpec spec = pendingGenes[i];
                Rect row = new Rect(0f, i * rowHeight, contentWidth, rowHeight);

                if (i % 2 == 1)
                {
                    Widgets.DrawLightHighlight(row);
                }

                // 点名字 = 载入回左侧编辑区
                Rect nameRect = new Rect(row.x, row.y + 1f, nameColumnWidth, rowHeight - 2f);
                if (Widgets.ButtonInvisible(nameRect))
                {
                    LoadGeneFromList(spec);
                    Messages.Message("CGET_LoadedToEditor".Translate(spec.Name), MessageTypeDefOf.SilentInput, false);
                }
                TooltipHandler.TipRegion(nameRect, "CGET_ClickToLoadTip".Translate());
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(nameRect.x + 6f, row.y, nameRect.width - 10f, rowHeight), spec.Name);

                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(row.x + typeColumnX, row.y, typeColumnWidth, rowHeight), GetGeneTypeShortLabel(spec.ColorType));
                Text.Anchor = TextAnchor.UpperLeft;

                if (Widgets.ButtonText(new Rect(row.x + deleteColumnX, row.y + 3f, deleteColumnWidth, rowHeight - 6f), "×"))
                {
                    pendingGenes.RemoveAt(i);
                    break;
                }
            }
            Widgets.EndScrollView();
        }

        private int GetExportedGeneFileCount()
        {
            if (Time.realtimeSinceStartup - exportedGeneCountRefreshTime > 1f)
            {
                exportedGeneCountRefreshTime = Time.realtimeSinceStartup;
                try
                {
                    string folder = GeneExporter.GeneDefsFolderPath;
                    exportedGeneFileCount = Directory.Exists(folder)
                        ? Directory.GetFiles(folder, "GegeXT_*.xml").Length
                        : 0;
                }
                catch (Exception)
                {
                    exportedGeneFileCount = 0;
                }
            }

            return exportedGeneFileCount;
        }

        private void DrawColorGenePalette(Rect rect)
        {
            int r = ParseRgbChannel(skinRedText, 200);
            int g = ParseRgbChannel(skinGreenText, 160);
            int b = ParseRgbChannel(skinBlueText, 120);

            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 24f),
                colorGeneType == GeneColorType.HairColor ? "CGET_HairPaletteHeader".Translate() : "CGET_SkinPaletteHeader".Translate());
            rect = new Rect(rect.x, rect.y + 28f, rect.width, Mathf.Max(rect.height - 28f, 24f));

            //HSV 色轮
            Rect wheelRect = new Rect(rect.x, rect.y, 108f, 108f);
            Color wheelColor = new Color(r / 255f, g / 255f, b / 255f, 1f);
            Widgets.HSVColorWheel(wheelRect, ref wheelColor, ref skinColorWheelDragging, null, "CustomGeneColorWheel");
            ApplyPickedSkinColor(wheelColor, r, g, b);

            //所有已加载颜色
            Rect gridRect = new Rect(rect.x + 116f, rect.y, Mathf.Max(rect.width - 116f, 40f), 108f);
            List<Color> colors = GetColorPaletteColors(colorGeneType);

            const float cell = 26f;
            int columns = Mathf.Max(1, Mathf.FloorToInt((gridRect.width - 16f) / cell));
            int rows = Mathf.Max(1, Mathf.CeilToInt(colors.Count / (float)columns));
            Rect viewRect = new Rect(0f, 0f, gridRect.width - 16f, rows * cell);

            Widgets.BeginScrollView(gridRect, ref skinPaletteScroll, viewRect);
            Color picked = new Color(r / 255f, g / 255f, b / 255f, 1f);
            for (int i = 0; i < colors.Count; i++)
            {
                Rect cellRect = new Rect((i % columns) * cell, (i / columns) * cell, cell, cell);
                Widgets.ColorBox(cellRect, ref picked, colors[i], 22, 2, null);
            }
            Widgets.EndScrollView();

            ApplyPickedSkinColor(picked, r, g, b);
        }

        private void ApplyPickedSkinColor(Color color, int currentR, int currentG, int currentB)
        {
            Color32 picked = color;
            if (picked.r == currentR && picked.g == currentG && picked.b == currentB)
            {
                return;
            }

            skinRedText = picked.r.ToString();
            skinGreenText = picked.g.ToString();
            skinBlueText = picked.b.ToString();
        }

        //做成色板
        private static List<Color> GetColorPaletteColors(GeneColorType type)
        {
            bool isHair = type == GeneColorType.HairColor;
            if (isHair && hairPaletteColorsCache != null)
            {
                return hairPaletteColorsCache;
            }
            if (!isHair && skinPaletteColorsCache != null)
            {
                return skinPaletteColorsCache;
            }

            List<Color> colors = new List<Color>();
            try
            {
                foreach (GeneDef gene in DefDatabase<GeneDef>.AllDefsListForReading)
                {
                    Color? overrideColor = isHair ? gene.hairColorOverride : gene.skinColorOverride;
                    if (!overrideColor.HasValue)
                    {
                        continue;
                    }

                    Color color = overrideColor.Value;
                    bool duplicate = false;
                    foreach (Color existing in colors)
                    {
                        if (Mathf.Approximately(existing.r, color.r)
                            && Mathf.Approximately(existing.g, color.g)
                            && Mathf.Approximately(existing.b, color.b))
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (!duplicate)
                    {
                        colors.Add(color);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[CustomGeneExporter] 收集颜色基因颜色失败: " + ex.Message);
            }

            if (colors.Count == 0)
            {
                //兜底
                for (int i = 0; i < 20; i++)
                {
                    float t = i / 19f;
                    if (isHair)
                    {
                        // 发色兜底
                        colors.Add(new Color(0.08f + 0.87f * t, 0.06f + 0.78f * t, 0.05f + 0.70f * t, 1f));
                    }
                    else
                    {
                        colors.Add(new Color(0.18f + 0.72f * t, 0.13f + 0.65f * t, 0.10f + 0.58f * t, 1f));
                    }
                }
            }

            colors.Sort(delegate (Color a, Color b)
            {
                float lightnessA = 0.299f * a.r + 0.587f * a.g + 0.114f * a.b;
                float lightnessB = 0.299f * b.r + 0.587f * b.g + 0.114f * b.b;
                return lightnessA.CompareTo(lightnessB);
            });

            if (isHair)
            {
                hairPaletteColorsCache = colors;
            }
            else
            {
                skinPaletteColorsCache = colors;
            }
            return colors;
        }

        // 基因类型文案
        private string GetColorGeneTypeLabel()
        {
            return GetGeneTypeLabel(colorGeneType);
        }

        private static string GetGeneTypeLabel(GeneColorType type)
        {
            if (type == GeneColorType.SkinColor)
            {
                return "CGET_GeneTypeSkin".Translate();
            }
            if (type == GeneColorType.HairColor)
            {
                return "CGET_GeneTypeHair".Translate();
            }
            return "CGET_GeneTypeFunctional".Translate();
        }

        private static string GetGeneTypeShortLabel(GeneColorType type)
        {
            if (type == GeneColorType.SkinColor)
            {
                return "CGET_GeneTypeShortSkin".Translate();
            }
            if (type == GeneColorType.HairColor)
            {
                return "CGET_GeneTypeShortHair".Translate();
            }
            return "CGET_GeneTypeShortFunctional".Translate();
        }

        // 皮肤 / 发色基因不带自定义 class
        private void ClearGeneClassForColorGene()
        {
            selectedGeneClass = null;
            selectedGeneClassMod = null;
            selectedClassCustomEffects.Clear();
        }

        //容错解析
        private static int ParseRgbChannel(string text, int fallback)
        {
            if (string.IsNullOrEmpty(text))
            {
                return fallback;
            }

            string trimmed = text.Trim();
            int intValue;
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
            {
                return Mathf.Clamp(intValue, 0, 255);
            }

            float floatValue;
            if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out floatValue))
            {
                return Mathf.Clamp(Mathf.RoundToInt(floatValue), 0, 255);
            }

            return fallback;
        }
        //预览图
        private string previewImagePath = "";
        private string lastLoadedPreviewPath = null;
        private Texture2D previewImageTexture = null;
        private Texture2D defaultPreviewTexture = null;

        //原版颜色基因图标
        private Texture2D skinColorIconTexture = null;
        private Texture2D hairColorIconTexture = null;
        private bool triedLoadSkinColorIcon = false;
        private bool triedLoadHairColorIcon = false;

        // 肤色 / 发色色板缓存
        private static List<Color> skinPaletteColorsCache = null;
        private static List<Color> hairPaletteColorsCache = null;

        //色板滚动与色轮拖拽状态
        private Vector2 skinPaletteScroll = Vector2.zero;
        private bool skinColorWheelDragging = false;
    }
}