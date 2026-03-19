using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using BadApple.Patches;

namespace BadApple;

[ModInitializer(nameof(Initialize))]
public class FastDrawImageMain
{
    private const string HarmonyId = "com.arpo35.fastdrawimg";

    public static void Initialize()
    {
        GD.Print("[FastDrawImg] === 静态图模式初始化 ===");

        try
        {
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

    [HarmonyPatch(typeof(NMapDrawings), "_UnhandledInput")]
    private static class MapDrawingsInputPatch
    {
        public static void Postfix(NMapDrawings __instance, InputEvent @event)
        {
            if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
                return;

            var scanner = GetScanner(__instance);
            if (scanner == null)
                return;

            bool ctrl = Input.IsKeyPressed(Key.Ctrl);
            bool shift = Input.IsKeyPressed(Key.Shift);

            if (ctrl && keyEvent.Keycode == Key.U)
            {
                scanner.OpenImportDialog();
                __instance.GetViewport()?.SetInputAsHandled();
                return;
            }

            if (ctrl && keyEvent.Keycode == Key.V)
            {
                scanner.PasteFromClipboard();
                __instance.GetViewport()?.SetInputAsHandled();
                return;
            }

            if (shift && keyEvent.Keycode == Key.U)
            {
                scanner.ClearCurrentImage();
                __instance.GetViewport()?.SetInputAsHandled();
                return;
            }

            if (keyEvent.Keycode == Key.U)
            {
                scanner.DrawCurrentImage();
                __instance.GetViewport()?.SetInputAsHandled();
            }
        }
    }
}
