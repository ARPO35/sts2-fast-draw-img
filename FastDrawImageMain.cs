using Godot;
using HarmonyLib;
using System.Reflection;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
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

    private static SubViewport? TryGetDrawViewport(NMapDrawings drawings)
        => drawings.FindChild("DrawViewport", true, false) as SubViewport;

    private static FastDrawImageScanner? GetScanner(NMapDrawings drawings)
    {
        SubViewport? drawViewport = TryGetDrawViewport(drawings);
        return drawViewport?.GetNodeOrNull<FastDrawImageScanner>(FastDrawImageScanner.NodeName);
    }

    private static ulong? TryGetPlayerId(object? state)
    {
        if (state == null)
            return null;

        var playerIdField = state.GetType().GetField("playerId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return playerIdField?.GetValue(state) is ulong playerId ? playerId : null;
    }

    private static void CleanupLegacyArtifacts(NMapDrawings drawings, SubViewport drawViewport)
    {
        Node? legacyScanner = drawings.FindChild(FastDrawImageScanner.NodeName, true, false);
        if (legacyScanner != null && legacyScanner != drawViewport.GetNodeOrNull<Node>(FastDrawImageScanner.NodeName))
            legacyScanner.QueueFree();

        drawings.FindChild(DrawAreaOverlay.NodeName, true, false)?.QueueFree();
        NGame.Instance?.FindChild(FastDrawImageScanner.UiLayerName, true, false)?.QueueFree();
    }

    [HarmonyPatch(typeof(NMapDrawings), "_Ready")]
    private static class MapDrawingsReadyPatch
    {
        public static void Postfix(NMapDrawings __instance)
        {
            SubViewport? drawViewport = TryGetDrawViewport(__instance);
            if (drawViewport == null)
            {
                FastDrawLog.Warn("未找到 DrawViewport，无法挂载图片绘制器");
                return;
            }

            if (drawViewport.GetNodeOrNull<FastDrawImageScanner>(FastDrawImageScanner.NodeName) != null)
                return;

            CleanupLegacyArtifacts(__instance, drawViewport);

            var scanner = new FastDrawImageScanner { Name = FastDrawImageScanner.NodeName };
            drawViewport.AddChild(scanner);
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

    [HarmonyPatch(typeof(NGame), "_Input")]
    private static class GlobalInputPatch
    {
        public static void Postfix(NGame __instance, InputEvent inputEvent)
        {
            if (inputEvent is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
                return;

            var drawingsNode = __instance.GetTree().Root.FindChild("Drawings", true, false) as NMapDrawings;
            if (drawingsNode == null)
                return;

            var scanner = GetScanner(drawingsNode);
            if (scanner == null)
                return;

            if (scanner.ProcessShortcutInput(keyEvent))
                __instance.GetViewport()?.SetInputAsHandled();
        }
    }
}
