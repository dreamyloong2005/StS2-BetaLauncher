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
            PatchHelper.Patch(
                harmony,
                typeof(NDevConsole),
                nameof(NDevConsole._Ready),
                prefix: PatchHelper.Method(typeof(NDevConsolePatches), nameof(ReadyPrefix))
            );

            PatchHelper.Log(
                "[NDevConsolePatch] 成功注入【运行时自修复】补丁（已解决移动端初始化时序问题）！"
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log("[NDevConsolePatch] 补丁注入失败: " + ex);
        }
    }

    public static bool ReadyPrefix(NDevConsole __instance)
    {
        try
        {
            var tr = Traverse.Create(__instance);

            var outputBuffer = __instance.GetNode<RichTextLabel>("OutputContainer/OutputBuffer");
            var tabBuffer = __instance.GetNode<RichTextLabel>("OutputContainer/TabBuffer");
            var inputContainer = __instance.GetNode<Control>("InputContainer");
            var inputBuffer = __instance.GetNode<LineEdit>(
                "InputContainer/InputBufferContainer/InputBuffer"
            );
            var promptLabel = __instance.GetNode<Label>("InputContainer/PromptLabel");
            var ghostTextLabel = __instance.GetNode<Label>(
                "InputContainer/InputBufferContainer/GhostText"
            );

            tr.Field("_outputBuffer").SetValue(outputBuffer);
            tr.Field("_tabBuffer").SetValue(tabBuffer);
            tr.Field("_inputContainer").SetValue(inputContainer);
            tr.Field("_inputBuffer").SetValue(inputBuffer);
            tr.Field("_promptLabel").SetValue(promptLabel);
            tr.Field("_ghostTextLabel").SetValue(ghostTextLabel);

            __instance.HideConsole();
            __instance.MakeHalfScreen();
            tr.Method("DisableTabBuffer").GetValue();
            tr.Method("HideGhostText").GetValue();
            inputBuffer.CaretBlink = true;
            tr.Method("UpdatePromptStyle").GetValue();

            // 采用 Godot 4 原生的 Connect + Callable 方式，规避反射私有委托（+=）带来的复杂性
            inputBuffer.Connect(
                LineEdit.SignalName.TextChanged,
                new Callable(__instance, "OnInputTextChanged")
            );

            tr.Method("PrintUsage").GetValue();

            return false;
        }
        catch (Exception ex)
        {
            GD.PushError($"NDevConsole _Ready Prefix 异常: {ex}");
            return true;
        }
    }
}
