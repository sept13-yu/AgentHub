using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentHub.Core.DocCore;

internal static class AgentRuleTemplates
{
    internal const string SharedReference = "%USERPROFILE%\\.agents\\AGENTS.md";
    internal const string ManagedComment = "本文件由 AgentHub 管理，改规则请编辑 `%USERPROFILE%\\.agents\\AGENTS.md`";
    internal const string PointerFileName = "pointer-template.md";

    private static readonly Regex ExtraMark = new(
        @"<!--\s*extra:([a-z][a-z0-9]*)\s*-->",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string PointerTemplatePath(string userProfile) =>
        Path.Combine(userProfile, ".agents", PointerFileName);

    public static string RenderShared(string libraryRoot)
    {
        var text = ReadEmbedded("SharedRules.md");
        return text.Replace("{{libraryRoot}}", libraryRoot, StringComparison.Ordinal);
    }

    public static string ReadDefaultPointer() => ReadEmbedded("Pointer.md");

    public static PointerTemplateInfo InspectPointerTemplate(string path, string? userText)
    {
        var customized = userText is not null;
        var valid = !customized || IsValidPointerTemplate(userText!);
        var source = customized && valid ? userText! : ReadEmbedded("Pointer.md");
        return new(path, customized, valid, MissingExtraWarnings(source));
    }

    public static string RenderReference(string agentId, string libraryRoot, string? userTemplate)
    {
        var (title, slug) = Identity(agentId);
        var parsed = ParsePointer(ResolvePointerSource(userTemplate));
        var body = ApplyVars(parsed.Body, title, slug, libraryRoot);
        parsed.Extras.TryGetValue(agentId, out var extraRaw);
        var extra = ApplyVars(extraRaw ?? "", title, slug, libraryRoot);

        var builder = new StringBuilder();
        builder.AppendLine($"<!-- {ManagedComment} -->");
        foreach (var line in SplitLines(body))
            builder.AppendLine(line);
        foreach (var line in SplitLines(extra))
            builder.AppendLine(line);
        return builder.ToString();
    }

    public static string RenderCursor(string libraryRoot, string? userTemplate) =>
        "---" + Environment.NewLine
        + "description: 打开并遵守共用规则" + Environment.NewLine
        + "alwaysApply: true" + Environment.NewLine
        + "---" + Environment.NewLine
        + Environment.NewLine
        + RenderReference("cursor", libraryRoot, userTemplate);

    public static bool LooksLikePointer(string text)
    {
        if (text.Contains(ManagedComment, StringComparison.Ordinal)) return true;
        var n = text.Replace('/', '\\');
        return n.Contains("打开并遵守", StringComparison.Ordinal)
            && (n.Contains(SharedReference, StringComparison.OrdinalIgnoreCase)
                || n.Contains("%USERPROFILE%\\.agents\\AGENTS.md", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsValidPointerTemplate(string text)
    {
        var n = text.Replace('/', '\\');
        return n.Contains("打开并遵守", StringComparison.Ordinal)
            && (n.Contains(SharedReference, StringComparison.OrdinalIgnoreCase)
                || n.Contains("%USERPROFILE%\\.agents\\AGENTS.md", StringComparison.OrdinalIgnoreCase));
    }

    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    public static bool SameText(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    public static string? ReplaceLibraryLine(string text, string libraryRoot)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var found = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("资料目录：", StringComparison.Ordinal)
                || line.StartsWith("枢纽：", StringComparison.Ordinal))
            {
                lines[i] = "资料目录：" + libraryRoot;
                found = true;
                break;
            }
        }
        if (!found) return null;
        var joined = string.Join("\n", lines);
        if (text.Contains("\r\n", StringComparison.Ordinal))
            return joined.Replace("\n", "\r\n");
        return joined;
    }

    private static string ResolvePointerSource(string? userTemplate) =>
        userTemplate is not null && IsValidPointerTemplate(userTemplate)
            ? userTemplate
            : ReadEmbedded("Pointer.md");

    private static IReadOnlyList<string> MissingExtraWarnings(string source)
    {
        var extras = ParsePointer(source).Extras;
        if (extras.TryGetValue("workbuddy", out var extra) && extra.Trim().Length > 0)
            return [];
        return ["WorkBuddy 差异块缺失，记忆目录约束不会注入"];
    }

    private static (string Body, Dictionary<string, string> Extras) ParsePointer(string text)
    {
        text = Normalize(text);
        var extras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = ExtraMark.Matches(text);
        if (matches.Count == 0)
            return (text.TrimEnd('\n'), extras);

        var body = text[..matches[0].Index].TrimEnd('\n');
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            extras[matches[i].Groups[1].Value] = text[start..end].Trim('\n');
        }
        return (body, extras);
    }

    private static (string Title, string Slug) Identity(string agentId) => agentId switch
    {
        "codex" => ("Codex", "Codex"),
        "dsh" => ("DSH", "Dsh"),
        "zcode" => ("ZCode", "ZCode"),
        "cursor" => ("Cursor", "Cursor"),
        "trae" => ("Trae", "Trae"),
        "workbuddy" => ("WorkBuddy", "WorkBuddy"),
        _ => throw new ArgumentOutOfRangeException(nameof(agentId)),
    };

    private static string ApplyVars(string text, string title, string slug, string libraryRoot) =>
        text.Replace("{{title}}", title, StringComparison.Ordinal)
            .Replace("{{slug}}", slug, StringComparison.Ordinal)
            .Replace("{{libraryRoot}}", libraryRoot, StringComparison.Ordinal);

    private static IEnumerable<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (var line in Normalize(text).TrimEnd('\n').Split('\n'))
            yield return line;
    }

    private static string ReadEmbedded(string fileName)
    {
        var asm = typeof(AgentRuleTemplates).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("找不到共用规则模板 " + fileName);
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("无法读取共用规则模板");
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
