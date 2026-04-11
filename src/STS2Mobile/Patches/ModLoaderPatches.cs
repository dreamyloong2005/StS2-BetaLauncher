using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
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
            PatchHelper.Patch(
                harmony,
                typeof(ModManager),
                "Initialize",
                transpiler: PatchHelper.Method(typeof(ModLoaderPatches), nameof(InitializeTranspiler))
            );
            PatchHelper.Patch(
                harmony,
                typeof(ModManager),
                "ReadSteamMods",
                prefix: PatchHelper.Method(typeof(ModLoaderPatches), nameof(ReadSteamModsPrefix))
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
        CreateAndAssignDevConsole();
    }

    public static IEnumerable<CodeInstruction> InitializeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var matcher = new CodeMatcher(instructions)
            .MatchStartForward(new CodeMatch(OpCodes.Ldstr, "mods"));   // 找到唯一一个 "mods" 字符串字面量

        if (matcher.IsValid)
        {
            // 当前位置在 ldstr "mods"
            // 前面一条指令是 ldloc directoryName
            // 后面是 call Path.Combine + stloc path
            matcher.Advance(-1);                    // 退到 ldloc directoryName
            matcher.RemoveInstructions(3);          // 删除：ldloc directoryName + ldstr "mods" + call Path.Combine
            var getter = AccessTools.PropertyGetter(typeof(AppPaths), nameof(AppPaths.ExternalModsDir));
            matcher.InsertAndAdvance(
                new CodeInstruction(OpCodes.Ldstr, getter)
            );
            // 现在 stloc path 会直接存入我们硬编码的完整路径
        }
        // 如果没找到（理论上不可能），保持原指令（防止崩溃）

        return matcher.InstructionEnumeration();
    }
    public static bool ReadSteamModsPrefix()
    {
        return false;
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
