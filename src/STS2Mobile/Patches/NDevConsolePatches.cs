using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Debug;

namespace STS2Mobile.Patches;

public static class NDevConsolePatches
{
    private static readonly BindingFlags AllInstance =
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Apply(Harmony harmony)
    {
        try
        {
            // 使用 PatchHelper.Patch 简洁风格注入三个前缀补丁
            PatchHelper.Patch(
                harmony,
                typeof(NDevConsole),
                "UpdateGhostText",
                prefix: PatchHelper.Method(
                    typeof(NDevConsolePatches),
                    nameof(UpdateGhostTextPrefix)
                )
            );
            PatchHelper.Patch(
                harmony,
                typeof(NDevConsole),
                "ProcessCommand",
                prefix: PatchHelper.Method(typeof(NDevConsolePatches), nameof(ProcessCommandPrefix))
            );
            PatchHelper.Patch(
                harmony,
                typeof(NDevConsole),
                "OnInputTextChanged",
                prefix: PatchHelper.Method(
                    typeof(NDevConsolePatches),
                    nameof(OnInputTextChangedPrefix)
                )
            );

            PatchHelper.Log("[NDevConsolePatch] 成功注入 NDevConsole 空值检查补丁！");
        }
        catch (Exception ex)
        {
            PatchHelper.Log("[NDevConsolePatch] 补丁注入失败: " + ex.ToString());
        }
    }

    // ========== 补丁方法必须为 public static（供 PatchHelper.Method 调用） ==========

    // UpdateGhostText 的前缀检查
    public static bool UpdateGhostTextPrefix(NDevConsole __instance)
    {
        try
        {
            var inputBuffer = GetPrivateField<LineEdit>(__instance, "_inputBuffer");
            var ghostLabel = GetPrivateField<Label>(__instance, "_ghostTextLabel");
            var devConsole = GetPrivateField<object>(__instance, "_devConsole");

            if (inputBuffer == null || ghostLabel == null || devConsole == null)
            {
                return false; // 跳过原方法
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ProcessCommand 的前缀检查
    public static bool ProcessCommandPrefix(NDevConsole __instance)
    {
        try
        {
            var inputBuffer = GetPrivateField<LineEdit>(__instance, "_inputBuffer");
            var outputBuffer = GetPrivateField<RichTextLabel>(__instance, "_outputBuffer");
            var devConsole = GetPrivateField<object>(__instance, "_devConsole");

            if (inputBuffer == null || outputBuffer == null || devConsole == null)
            {
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // OnInputTextChanged 的前缀检查
    public static bool OnInputTextChangedPrefix(NDevConsole __instance, string newText)
    {
        try
        {
            var inputBuffer = GetPrivateField<LineEdit>(__instance, "_inputBuffer");
            var ghostLabel = GetPrivateField<Label>(__instance, "_ghostTextLabel");
            var devConsole = GetPrivateField<object>(__instance, "_devConsole");

            if (inputBuffer == null || ghostLabel == null || devConsole == null)
            {
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // 辅助方法：获取私有字段值（保持原样，可保留 private）
    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        if (instance == null)
            return default;
        var field = instance
            .GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
            return default;
        var value = field.GetValue(instance);
        return value is T t ? t : default;
    }
}
