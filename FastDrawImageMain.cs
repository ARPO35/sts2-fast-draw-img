using Godot;
using HarmonyLib;
using System.Reflection;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using FastDrawImg.Patches;

namespace FastDrawImg;

[ModInitializer(nameof(Initialize))]
public class FastDrawImageMain
{
    private const string HarmonyId = "com.arpo35.fastdrawimg";

    public static void Initialize()
    {
        GD.Print("[FastDrawImg] === 图片模式初始化 ===");

        try
        {
            FastDrawShortcutConfig.Load();
            var harmony = new Harmony(HarmonyId);
            harmony.PatchAll();
            GD.Print("[FastDrawImg] Harmony 补丁注入成功");
        }
        catch (System.Exception e)
        {
            GD.PushError("[FastDrawImg] Harmony 注入失败: " + e.Message);
        }
    }

    private static FastDrawImageScanner? GetScanner(NMapDrawings drawings)
        => drawings.GetNodeOrNull<FastDrawImageScanner>(FastDrawImageScanner.NodeName);

    private static ulong? TryGetPlayerId(object? state)
    {
        if (state == null)
            return null;

        var playerIdField = state.GetType().GetField("playerId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return playerIdField?.GetValue(state) is ulong playerId ? playerId : null;
    }

    [HarmonyPatch(typeof(NMapDrawings), "_Ready")]
    private static class MapDrawingsReadyPatch
    {
        public static void Postfix(NMapDrawings __instance)
        {
            if (GetScanner(__instance) != null)
                return;

            var scanner = new FastDrawImageScanner { Name = FastDrawImageScanner.NodeName };
            __instance.AddChild(scanner);
            scanner.Initialize(__instance);
            GD.Print("[FastDrawImg] 图像绘制器已挂载");
        }
    }

    [HarmonyPatch(typeof(NMapDrawings), nameof(NMapDrawings.ClearAllLines))]
    private static class MapDrawingsClearAllLinesPatch
    {
        public static void Postfix(NMapDrawings __instance)
            => GetScanner(__instance)?.OnMapCleared();
    }

    [HarmonyPatch(typeof(NMapDrawings), "ClearAllLinesForPlayer")]
    private static class MapDrawingsClearAllLinesForPlayerPatch
    {
        public static void Postfix(NMapDrawings __instance, object state)
        {
            var playerId = TryGetPlayerId(state);
            if (playerId.HasValue)
                GetScanner(__instance)?.OnPlayerMapCleared(playerId.Value);
        }
    }

    [HarmonyPatch(typeof(NMapDrawings), "_UnhandledInput")]
    private static class MapDrawingsInputPatch
    {
        public static void Postfix(NMapDrawings __instance, InputEvent @event)
        {
            var scanner = GetScanner(__instance);
            if (scanner == null)
                return;

            if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
                return;

            FastDrawShortcuts shortcuts = FastDrawShortcutConfig.Current;

            if (shortcuts.Matches(FastDrawShortcutAction.CancelSelection, keyEvent))
            {
                if (scanner.CancelAreaSelectionShortcut())
                    __instance.GetViewport()?.SetInputAsHandled();
                return;
            }

            if (shortcuts.Matches(FastDrawShortcutAction.CaptureSelectionStart, keyEvent))
            {
                scanner.CaptureSelectionStart();
                __instance.GetViewport()?.SetInputAsHandled();
                return;
            }

            if (shortcuts.Matches(FastDrawShortcutAction.CaptureSelectionEnd, keyEvent))
            {
                scanner.CaptureSelectionEnd();
                __instance.GetViewport()?.SetInputAsHandled();
                return;
            }

            if (shortcuts.Matches(FastDrawShortcutAction.ImportImage, keyEvent))
            {
                if (scanner.IsSelectionModeActive)
                    scanner.NotifySelectionModeBlocked();
                else
                    scanner.OpenImportDialog();

                __instance.GetViewport()?.SetInputAsHandled();
                return;
            }

            if (shortcuts.Matches(FastDrawShortcutAction.PasteImagePath, keyEvent))
            {
                if (scanner.IsSelectionModeActive)
                    scanner.NotifySelectionModeBlocked();
                else
                    scanner.PasteFromClipboard();

                __instance.GetViewport()?.SetInputAsHandled();
                return;
            }

            if (shortcuts.Matches(FastDrawShortcutAction.ClearCurrentImage, keyEvent))
            {
                if (scanner.IsSelectionModeActive)
                    scanner.NotifySelectionModeBlocked();
                else
                    scanner.ClearCurrentImage();

                __instance.GetViewport()?.SetInputAsHandled();
                return;
            }

            if (shortcuts.Matches(FastDrawShortcutAction.DrawCurrentImage, keyEvent))
            {
                if (scanner.IsSelectionModeActive)
                    scanner.NotifySelectionModeBlocked();
                else
                    scanner.DrawCurrentImage();

                __instance.GetViewport()?.SetInputAsHandled();
            }
        }
    }
}
