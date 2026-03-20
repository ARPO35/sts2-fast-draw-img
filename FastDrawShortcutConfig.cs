using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastDrawImg;

public enum FastDrawShortcutAction
{
    ImportImage,
    PasteImagePath,
    DrawCurrentImage,
    ClearCurrentImage,
    CaptureSelectionStart,
    CaptureSelectionEnd,
    CancelSelection
}

public sealed class FastDrawImgConfigFile
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("has_pck")]
    public bool? HasPck { get; init; }

    [JsonPropertyName("has_dll")]
    public bool? HasDll { get; init; }

    [JsonPropertyName("dependencies")]
    public string[]? Dependencies { get; init; }

    [JsonPropertyName("affects_gameplay")]
    public bool? AffectsGameplay { get; init; }

    [JsonPropertyName("debugLogEnabled")]
    public bool? DebugLogEnabled { get; init; }

    [JsonPropertyName("shortcuts")]
    public FastDrawShortcutConfigSection? Shortcuts { get; init; }
}

public sealed class FastDrawShortcutConfigSection
{
    [JsonPropertyName("importImage")]
    public string? ImportImage { get; init; }

    [JsonPropertyName("pasteImagePath")]
    public string? PasteImagePath { get; init; }

    [JsonPropertyName("drawCurrentImage")]
    public string? DrawCurrentImage { get; init; }

    [JsonPropertyName("clearCurrentImage")]
    public string? ClearCurrentImage { get; init; }

    [JsonPropertyName("captureSelectionStart")]
    public string? CaptureSelectionStart { get; init; }

    [JsonPropertyName("captureSelectionEnd")]
    public string? CaptureSelectionEnd { get; init; }

    [JsonPropertyName("cancelSelection")]
    public string? CancelSelection { get; init; }
}

public readonly record struct ShortcutBinding(Key Keycode, bool Ctrl, bool Shift, bool Alt)
{
    public bool Matches(InputEventKey keyEvent)
        => keyEvent.Keycode == Keycode
           && keyEvent.CtrlPressed == Ctrl
           && keyEvent.ShiftPressed == Shift
           && keyEvent.AltPressed == Alt;

    public string Signature => $"{(int)Keycode}:{Ctrl}:{Shift}:{Alt}";

    public string DisplayText
    {
        get
        {
            var parts = new List<string>(4);
            if (Ctrl)
                parts.Add("Ctrl");
            if (Shift)
                parts.Add("Shift");
            if (Alt)
                parts.Add("Alt");
            parts.Add(FormatKey(Keycode));
            return string.Join("+", parts);
        }
    }

    private static string FormatKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
            return key.ToString().ToUpperInvariant();

        if (key is >= Key.Key0 and <= Key.Key9)
            return ((int)key - (int)Key.Key0).ToString();

        return key switch
        {
            Key.Bracketleft => "[",
            Key.Bracketright => "]",
            Key.Escape => "Escape",
            _ => key.ToString()
        };
    }
}

public sealed class FastDrawShortcuts
{
    private readonly Dictionary<FastDrawShortcutAction, ShortcutBinding> _bindings;

    private FastDrawShortcuts(Dictionary<FastDrawShortcutAction, ShortcutBinding> bindings)
        => _bindings = bindings;

    public ShortcutBinding GetBinding(FastDrawShortcutAction action) => _bindings[action];

    public bool Matches(FastDrawShortcutAction action, InputEventKey keyEvent)
        => GetBinding(action).Matches(keyEvent);

    public string Describe(FastDrawShortcutAction action) => GetBinding(action).DisplayText;

    public static FastDrawShortcuts CreateDefault()
    {
        var bindings = new Dictionary<FastDrawShortcutAction, ShortcutBinding>(OrderedSpecs.Length);
        foreach (var spec in OrderedSpecs)
            bindings[spec.Action] = spec.DefaultBinding;
        return new FastDrawShortcuts(bindings);
    }

    public static FastDrawShortcuts FromConfig(FastDrawShortcutConfigSection? config)
    {
        var bindings = new Dictionary<FastDrawShortcutAction, ShortcutBinding>(OrderedSpecs.Length);
        var usedBindings = new Dictionary<string, FastDrawShortcutAction>(OrderedSpecs.Length, StringComparer.Ordinal);

        foreach (var spec in OrderedSpecs)
        {
            string? configuredValue = GetConfiguredValue(config, spec.Action);
            ShortcutBinding binding = spec.DefaultBinding;
            bool fallbackToDefault = true;

            if (!string.IsNullOrWhiteSpace(configuredValue))
            {
                if (!TryParseBinding(configuredValue, out ShortcutBinding parsedBinding, out string error))
                {
                    FastDrawLog.Warn($"快捷键 {spec.ConfigName}=\"{configuredValue}\" 无效：{error}，回退默认值 {spec.DefaultBinding.DisplayText}");
                }
                else if (usedBindings.TryGetValue(parsedBinding.Signature, out FastDrawShortcutAction duplicateAction))
                {
                    FastDrawLog.Warn($"快捷键 {spec.ConfigName}=\"{configuredValue}\" 与 {GetConfigName(duplicateAction)} 冲突，回退默认值 {spec.DefaultBinding.DisplayText}");
                }
                else
                {
                    binding = parsedBinding;
                    fallbackToDefault = false;
                }
            }

            if (fallbackToDefault && usedBindings.TryGetValue(binding.Signature, out FastDrawShortcutAction defaultConflictAction))
            {
                FastDrawLog.Warn($"快捷键 {spec.ConfigName} 回退默认值 {binding.DisplayText} 后仍与 {GetConfigName(defaultConflictAction)} 冲突，请检查 FastDrawImg.json");
            }

            bindings[spec.Action] = binding;
            if (!usedBindings.ContainsKey(binding.Signature))
                usedBindings.Add(binding.Signature, spec.Action);
        }

        return new FastDrawShortcuts(bindings);
    }

    private static bool TryParseBinding(string rawBinding, out ShortcutBinding binding, out string error)
    {
        binding = default;
        error = string.Empty;

        string[] parts = rawBinding.Split('+');
        bool ctrl = false;
        bool shift = false;
        bool alt = false;
        Key? keycode = null;

        foreach (string part in parts)
        {
            string token = part.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                error = "存在空的按键片段";
                return false;
            }

            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase))
            {
                if (ctrl)
                {
                    error = "Ctrl 重复出现";
                    return false;
                }

                ctrl = true;
                continue;
            }

            if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                if (shift)
                {
                    error = "Shift 重复出现";
                    return false;
                }

                shift = true;
                continue;
            }

            if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                if (alt)
                {
                    error = "Alt 重复出现";
                    return false;
                }

                alt = true;
                continue;
            }

            if (keycode.HasValue)
            {
                error = "只能配置一个主键";
                return false;
            }

            if (!TryResolveKey(token, out Key resolvedKey))
            {
                error = $"无法识别主键 {token}";
                return false;
            }

            keycode = resolvedKey;
        }

        if (!keycode.HasValue)
        {
            error = "缺少主键";
            return false;
        }

        binding = new ShortcutBinding(keycode.Value, ctrl, shift, alt);
        return true;
    }

    private static bool TryResolveKey(string token, out Key keycode)
    {
        if (KeyAliases.TryGetValue(token, out keycode))
            return true;

        return Enum.TryParse(token, true, out keycode);
    }

    private static string? GetConfiguredValue(FastDrawShortcutConfigSection? config, FastDrawShortcutAction action)
        => action switch
        {
            FastDrawShortcutAction.ImportImage => config?.ImportImage,
            FastDrawShortcutAction.PasteImagePath => config?.PasteImagePath,
            FastDrawShortcutAction.DrawCurrentImage => config?.DrawCurrentImage,
            FastDrawShortcutAction.ClearCurrentImage => config?.ClearCurrentImage,
            FastDrawShortcutAction.CaptureSelectionStart => config?.CaptureSelectionStart,
            FastDrawShortcutAction.CaptureSelectionEnd => config?.CaptureSelectionEnd,
            FastDrawShortcutAction.CancelSelection => config?.CancelSelection,
            _ => null
        };

    private static string GetConfigName(FastDrawShortcutAction action)
        => action switch
        {
            FastDrawShortcutAction.ImportImage => "importImage",
            FastDrawShortcutAction.PasteImagePath => "pasteImagePath",
            FastDrawShortcutAction.DrawCurrentImage => "drawCurrentImage",
            FastDrawShortcutAction.ClearCurrentImage => "clearCurrentImage",
            FastDrawShortcutAction.CaptureSelectionStart => "captureSelectionStart",
            FastDrawShortcutAction.CaptureSelectionEnd => "captureSelectionEnd",
            FastDrawShortcutAction.CancelSelection => "cancelSelection",
            _ => action.ToString()
        };

    private sealed record ShortcutSpec(FastDrawShortcutAction Action, string ConfigName, string DefaultText, ShortcutBinding DefaultBinding);

    private static readonly Dictionary<string, Key> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Esc"] = Key.Escape,
        ["Escape"] = Key.Escape,
        ["["] = Key.Bracketleft,
        ["]"] = Key.Bracketright,
        ["0"] = Key.Key0,
        ["1"] = Key.Key1,
        ["2"] = Key.Key2,
        ["3"] = Key.Key3,
        ["4"] = Key.Key4,
        ["5"] = Key.Key5,
        ["6"] = Key.Key6,
        ["7"] = Key.Key7,
        ["8"] = Key.Key8,
        ["9"] = Key.Key9
    };

    private static readonly ShortcutSpec[] OrderedSpecs = CreateSpecs();

    private static ShortcutSpec[] CreateSpecs()
        => new[]
        {
            CreateSpec(FastDrawShortcutAction.ImportImage, "importImage", "Ctrl+U"),
            CreateSpec(FastDrawShortcutAction.PasteImagePath, "pasteImagePath", "Ctrl+V"),
            CreateSpec(FastDrawShortcutAction.DrawCurrentImage, "drawCurrentImage", "U"),
            CreateSpec(FastDrawShortcutAction.ClearCurrentImage, "clearCurrentImage", "Shift+U"),
            CreateSpec(FastDrawShortcutAction.CaptureSelectionStart, "captureSelectionStart", "["),
            CreateSpec(FastDrawShortcutAction.CaptureSelectionEnd, "captureSelectionEnd", "]"),
            CreateSpec(FastDrawShortcutAction.CancelSelection, "cancelSelection", "Escape")
        };

    private static ShortcutSpec CreateSpec(FastDrawShortcutAction action, string configName, string defaultText)
    {
        if (!TryParseBinding(defaultText, out ShortcutBinding binding, out string error))
            throw new InvalidOperationException($"默认快捷键 {configName}={defaultText} 解析失败：{error}");

        return new ShortcutSpec(action, configName, defaultText, binding);
    }
}

public static class FastDrawShortcutConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static FastDrawShortcuts Current { get; private set; } = FastDrawShortcuts.CreateDefault();

    public static void Load()
    {
        string configPath = ResolveConfigPath();
        string baseDirectory = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
        if (!File.Exists(configPath))
        {
            FastDrawLog.Configure(baseDirectory, enabled: false);
            Current = FastDrawShortcuts.CreateDefault();
            FastDrawLog.Warn($"未找到快捷键配置文件 {configPath}，使用默认快捷键");
            return;
        }

        try
        {
            string json = File.ReadAllText(configPath);
            FastDrawImgConfigFile? config = JsonSerializer.Deserialize<FastDrawImgConfigFile>(json, JsonOptions);
            FastDrawLog.Configure(baseDirectory, config?.DebugLogEnabled ?? false);
            Current = FastDrawShortcuts.FromConfig(config?.Shortcuts);
            FastDrawLog.Debug($"配置已载入: path={configPath}, debugLogEnabled={FastDrawLog.IsDebugEnabled}");
            GD.Print($"[FastDrawImg] 快捷键配置已载入: {configPath}");
        }
        catch (Exception ex)
        {
            FastDrawLog.Configure(baseDirectory, enabled: false);
            Current = FastDrawShortcuts.CreateDefault();
            FastDrawLog.Warn($"读取快捷键配置失败，使用默认快捷键: {ex.Message}");
        }
    }

    private static string ResolveConfigPath()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? baseDirectory = Path.GetDirectoryName(assembly.Location);
        if (string.IsNullOrEmpty(baseDirectory))
            baseDirectory = AppContext.BaseDirectory;

        return Path.Combine(baseDirectory, $"{assembly.GetName().Name}.json");
    }
}
