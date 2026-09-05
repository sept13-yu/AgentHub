using System.IO;
using System.Text;

namespace AgentHub.Core.DocCore;

internal static class AgentRuleTemplates
{
    internal const string SharedReference = "%USERPROFILE%\\.agents\\AGENTS.md";
    internal const string ManagedComment = "本文件由 AgentHub 管理，改规则请编辑 `%USERPROFILE%\\.agents\\AGENTS.md`";

    public static string RenderShared(string libraryRoot)
    {
        var text = ReadEmbedded("SharedRules.md");
        return text.Replace("{{libraryRoot}}", libraryRoot, StringComparison.Ordinal);
    }

    public static string RenderReference(string agentId, string libraryRoot)
    {
        var (title, slug, extra) = agentId switch
        {
            "codex" => ("Codex", "Codex", ""),
            "dsh" => ("DSH", "Dsh", ""),
            "zcode" => ("ZCode", "ZCode", ""),
            "cursor" => ("Cursor", "Cursor", "- 展示计划用 Canvas（其余各家用普通 Markdown）"),
            "trae" => ("Trae", "Trae", "- 产品名 TraeWork CN / TRAE SOLO CN，落盘目录写 `Trae`"),
            "workbuddy" => ("WorkBuddy", "WorkBuddy",
                $"- 自动生成的记忆文件夹 `.workbuddy`（含 memory/）只允许放在 `{Path.Combine(libraryRoot, "SandBox", "WorkBuddy")}` 下，不落在业务仓库"),
            _ => throw new ArgumentOutOfRangeException(nameof(agentId)),
        };
        var builder = new StringBuilder()
            .AppendLine($"<!-- {ManagedComment} -->")
            .AppendLine($"打开并遵守 `{SharedReference}`（通用行为、外置文档、Skill 落盘都以它为准）。本文件只写 {title} 的差异。")
            .AppendLine()
            .AppendLine($"- 当前 `<Agent>` 是 `{slug}`");
        if (extra.Length > 0) builder.AppendLine(extra);
        return builder.ToString();
    }

    public static string RenderCursor(string libraryRoot) =>
        "---" + Environment.NewLine
        + "description: 打开并遵守共用规则" + Environment.NewLine
        + "alwaysApply: true" + Environment.NewLine
        + "---" + Environment.NewLine
        + Environment.NewLine
        + RenderReference("cursor", libraryRoot);

    public static bool LooksLikePointer(string text)
    {
        if (text.Contains(ManagedComment, StringComparison.Ordinal)) return true;
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
