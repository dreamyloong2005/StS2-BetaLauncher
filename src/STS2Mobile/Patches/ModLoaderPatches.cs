using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;

namespace STS2Mobile.Patches;

public static class ModLoaderPatches
{
    private static readonly BindingFlags AllStatic =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Apply(Harmony harmony)
    {
        try
        {
            PatchHelper.Patch(
                harmony,
                typeof(ModManager),
                "Initialize",
                postfix: PatchHelper.Method(typeof(ModLoaderPatches), nameof(InitializePostfix))
            );
            PatchHelper.Log("[ModLoader] 成功注入 ModManager.Initialize 后置补丁！");
        }
        catch (Exception ex)
        {
            PatchHelper.Log("[ModLoader] 补丁注入失败: " + ex.ToString());
        }
    }

    public static void InitializePostfix()
    {
        PatchHelper.Log("[ModLoader] 开始执行外部 Mod 扫描流程...");
        try
        {
            string targetDir = null;

            // 1. 优先尝试外部公共目录（需要存储权限）
            if (AppPaths.HasStoragePermission())
            {
                string externalDir = AppPaths.ExternalModsDir;
                if (!string.IsNullOrEmpty(externalDir))
                {
                    if (Directory.Exists(externalDir))
                    {
                        targetDir = externalDir;
                        PatchHelper.Log($"[ModLoader] 使用外部目录: {targetDir}");
                    }
                    else
                    {
                        PatchHelper.Log($"[ModLoader] 外部目录不存在，尝试创建: {externalDir}");
                        try
                        {
                            Directory.CreateDirectory(externalDir);
                            targetDir = externalDir;
                            PatchHelper.Log($"[ModLoader] 已创建外部目录: {targetDir}");
                        }
                        catch (Exception ex)
                        {
                            PatchHelper.Log($"[ModLoader] 创建外部目录失败: {ex.Message}");
                        }
                    }
                }
            }
            else
            {
                PatchHelper.Log(
                    "[ModLoader] 无存储权限，无法使用外部目录。如需从外部加载 Mod，请授予权限。"
                );
            }

            // 2. 保底方案：使用 Godot 沙盒目录 user://game/mods
            if (string.IsNullOrEmpty(targetDir))
            {
                string fallbackDir = "user://game/mods";
                string globalFallback = ProjectSettings.GlobalizePath(fallbackDir);
                PatchHelper.Log($"[ModLoader] 尝试使用沙盒保底目录: {globalFallback}");

                if (!Directory.Exists(globalFallback))
                {
                    try
                    {
                        Directory.CreateDirectory(globalFallback);
                        PatchHelper.Log($"[ModLoader] 已创建沙盒目录: {globalFallback}");
                    }
                    catch (Exception ex)
                    {
                        PatchHelper.Log($"[ModLoader] 创建沙盒目录失败: {ex.Message}");
                        return;
                    }
                }
                targetDir = globalFallback;
            }

            PatchHelper.Log($"[ModLoader] 最终扫描目录: {targetDir}");

            // 3. 调用游戏原生的 ReadModsInDirRecursive 读取 .json 文件
            var readMethod = typeof(ModManager).GetMethod("ReadModsInDirRecursive", AllStatic);
            if (readMethod == null)
            {
                PatchHelper.Log("[ModLoader] 找不到 ReadModsInDirRecursive 方法！");
                return;
            }

            var newModsList = new List<Mod>();
            readMethod.Invoke(
                null,
                new object[] { targetDir, ModSource.ModsDirectory, newModsList }
            );

            PatchHelper.Log($"[ModLoader] 扫描结束，共发现 {newModsList.Count} 个外部模组。");
            if (newModsList.Count == 0)
                return;

            // 获取 settings 和 ModList 属性
            var settingsField = typeof(ModManager).GetField("_settings", AllStatic);
            var settings = settingsField?.GetValue(null);

            var modListProp = settings
                ?.GetType()
                .GetProperty("ModList", BindingFlags.Public | BindingFlags.Instance);
            object modList = modListProp?.GetValue(settings);

            var settingsSaveModType = typeof(ModManager).Assembly.GetType(
                "MegaCrit.Sts2.Core.Modding.SettingsSaveMod"
            );
            if (settingsSaveModType == null)
            {
                PatchHelper.Log("[ModLoader] 无法找到 SettingsSaveMod 类型，跳过设置更新");
            }
            else if (settings != null && newModsList.Count > 0)
            {
                // 如果 ModList 为空，创建一个新的
                if (modList == null)
                {
                    var listType = typeof(List<>).MakeGenericType(settingsSaveModType);
                    modList = Activator.CreateInstance(listType);
                }

                var addMethod = modList
                    .GetType()
                    .GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
                var idProp = settingsSaveModType.GetProperty(
                    "Id",
                    BindingFlags.Public | BindingFlags.Instance
                );
                var isEnabledProp = settingsSaveModType.GetProperty(
                    "IsEnabled",
                    BindingFlags.Public | BindingFlags.Instance
                );

                // 1. 先收集当前设置里已有的模组 ID（避免重复添加）
                var existingIds = new HashSet<string>();
                if (modList != null && idProp != null)
                {
                    var countProp = modList.GetType().GetProperty("Count");
                    int count = (int)(countProp?.GetValue(modList) ?? 0);
                    var itemProp = modList.GetType().GetProperty("Item", new[] { typeof(int) });

                    for (int i = 0; i < count; i++)
                    {
                        var entry = itemProp?.GetValue(modList, new object[] { i });
                        if (entry != null)
                        {
                            var id = idProp.GetValue(entry) as string;
                            if (!string.IsNullOrEmpty(id))
                                existingIds.Add(id);
                        }
                    }
                }

                // 2. 只对“全新”模组（设置里不存在的）才添加并默认 Enabled=true
                //    已存在的模组保持玩家原来的 IsEnabled 设置（不覆盖）
                int newlyAdded = 0;
                foreach (var newMod in newModsList)
                {
                    if (newMod.manifest?.id == null)
                        continue;

                    string modId = newMod.manifest.id;

                    if (existingIds.Contains(modId))
                    {
                        PatchHelper.Log(
                            $"[ModLoader] 模组 {modId} 已存在于设置中，保留玩家原有启用/禁用状态"
                        );
                        continue;
                    }

                    // 新模组 → 添加并默认启用
                    var settingsSaveMod = Activator.CreateInstance(
                        settingsSaveModType,
                        [newMod]
                    );
                    isEnabledProp?.SetValue(settingsSaveMod, true);

                    addMethod?.Invoke(modList, [settingsSaveMod]);
                    existingIds.Add(modId); // 防止重复
                    newlyAdded++;
                }

                if (newlyAdded > 0)
                {
                    modListProp?.SetValue(settings, modList);
                    PatchHelper.Log(
                        $"[ModLoader] 已将 {newlyAdded} 个**全新**模组加入设置列表（Enabled=true），玩家已禁用的模组保持不变"
                    );
                }
                else
                {
                    PatchHelper.Log("[ModLoader] 所有扫描到的模组都已在设置列表中，无需新增");
                }
            }

            // 4. 突破运行时限制：临时将 _initialized 设为 false
            var initField = typeof(ModManager).GetField("_initialized", AllStatic);
            initField?.SetValue(null, false);

            // 5. 重新排序模组（解决依赖关系）
            var sortMethod = typeof(ModManager).GetMethod("SortModList", AllStatic);
            object modListForSort = modList; // 直接复用上面已经处理好的列表
            if (modListForSort == null)
            {
                var listType = typeof(List<>).MakeGenericType(settingsSaveModType);
                modListForSort = Activator.CreateInstance(listType);
            }

            PatchHelper.Log("[ModLoader] 正在重新整理模组依赖链...");
            sortMethod?.Invoke(null, new object[] { modListForSort });

            // 6. 执行模组加载
            var tryLoadMethod = typeof(ModManager).GetMethod("TryLoadMod", AllStatic);
            var modsField = typeof(ModManager).GetField("_mods", AllStatic);
            var allMods = (List<Mod>)(modsField?.GetValue(null));

            if (allMods != null)
            {
                foreach (var mod in allMods)
                {
                    if (mod.state == ModLoadState.None)
                    {
                        PatchHelper.Log($"[ModLoader] 尝试加载安卓端注入模组: {mod.manifest?.id}");
                        tryLoadMethod?.Invoke(null, new object[] { mod });
                    }
                }
            }

            // 7. 恢复正常运行状态
            initField?.SetValue(null, true);
            PatchHelper.Log("[ModLoader] 外部模组注入流程全部完毕！");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ModLoader] 外部模组注入过程发生致命崩溃: \n{ex}");
        }
        finally
        {
            CreateAndAssignDevConsole();
        }
    }

    private static void CreateAndAssignDevConsole()
    {
        try
        {
            // 获取 NDevConsole 实例（静态属性 Instance）
            var consoleInstance = NDevConsole.Instance;
            if (consoleInstance == null)
            {
                PatchHelper.Log("[ModLoader] NDevConsole 实例尚未创建，延迟重试");
                // 延迟一帧再试（因为 _Ready 可能还没执行完）
                Callable.From(() => CreateAndAssignDevConsole()).CallDeferred();
                return;
            }

            // 计算 shouldAllowDebugCommands（与原逻辑一致）
            bool hasFullConsole = false;
            try
            {
                hasFullConsole = SaveManager.Instance?.SettingsSave?.FullConsole ?? false;
            }
            catch { }
            bool shouldAllowDebugCommands =
                OS.HasFeature("editor")
                || TestMode.IsOn
                || ModManager.IsRunningModded()
                || hasFullConsole;

            // 创建 DevConsole 实例
            var devConsole = new DevConsole(shouldAllowDebugCommands);

            // 通过反射赋值给私有字段 _devConsole
            var field = typeof(NDevConsole).GetField(
                "_devConsole",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            field?.SetValue(consoleInstance, devConsole);

            PatchHelper.Log("[ModLoader] DevConsole 已创建并赋值给 NDevConsole");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ModLoader] 创建 DevConsole 失败: {ex}");
        }
    }
}
