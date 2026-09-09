using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentHub.Core.DocCore;

/// <summary>v4 单文件母本：~/.agents/AGENTS.md 同时是内容与模板（正文 + 尾部 extra 差异块）。
/// v4.1：管理说明收进首行 HTML 注释头，渲染期剔除——各家可见内容只有规则本体、slug 行、自家差异块。</summary>
internal static class AgentRuleTemplates
{
    internal const string SharedReference = "%USERPROFILE%\\.agents\\AGENTS.md";
    internal const string ManagedComment = "本文件由 AgentHub 管理，改规则请编辑 `%USERPROFILE%\\.agents\\AGENTS.md`";
    internal const string RetiredPointerFileName = "pointer-template.md";

    /// <summary>母本管理注释的识别标记；IsValidMaster 以它判母本有效性，渲染期整行剔除。</summary>
    internal const string MasterMarker = "AgentHub 管理的共用规则母本";

    /// <summary>母本首行管理注释（给人看的说明 + 识别标记）。嵌入种子 SharedRules.md 首行是同一文本，改时两处同步。</summary>
    internal const string MasterManagedComment =
        "<!-- " + MasterMarker + "：本文件是各家 Agent 共用规则的唯一母本，正文与尾部 extra 差异块由 AgentHub 渲染进各家本机规则文件；改规则改这里，再到 AgentHub 规则页点「更新」。 -->";

    /// <summary>历代种子落在正文里的管理说明行：迁移时逐字命中才删，用户改过则保留。</summary>
    private static readonly string[] SeededMetaLines =
    [
        "各家本机规则只负责指向本文件并写自家差异；改这里一处，所有 Agent 生效。",
        "各家自己的规则文件只写一句：打开并遵守这份。改这里一处，所有 Agent 都生效。",
        "本文件是唯一母本：正文各家共用，尾部的 `extra:` 差异块只注入对应 Agent。改这里一处，所有 Agent 生效。",
    ];

    private static readonly Regex ExtraMark = new(
        @"<!--\s*extra:([a-z][a-z0-9]*)\s*-->",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>退役模板文件路径（v4 前的外部覆盖模板，存在即归档删除）。</summary>
    public static string RetiredPointerTemplatePath(string userProfile) =>
        Path.Combine(userProfile, ".agents", RetiredPointerFileName);

    /// <summary>首次播种：嵌入种子只把「资料目录：」行落成实际路径；extra 块里的 {{libraryRoot}} 保留给渲染期替换。</summary>
    public static string RenderShared(string libraryRoot) =>
        ReplaceLibraryLine(ReadEmbedded("SharedRules.md"), libraryRoot)
        ?? throw new InvalidDataException("共用规则种子缺少「资料目录：」行");

    /// <summary>识别链保护：母本必须带管理注释头（MasterMarker）。各家渲染产物靠代码前置的 ManagedComment 识别，不依赖母本内容。</summary>
    public static bool IsValidMaster(string text) =>
        text.Contains(MasterMarker, StringComparison.Ordinal);

    /// <summary>旧版母本迁成 v4.1：管理说明收进注释头（渲染期剔除），正文只留规则本体。
    /// 兼容 v4（首行指针头）与 v4 前纯正文两种旧版；认不出的内容返回 null（走种子回退）。</summary>
    public static string? MigrateMaster(string legacyText, string? userTemplate)
    {
        if (IsValidMaster(legacyText)) return null;
        var (body, legacyExtras) = ParsePointer(legacyText);
        var lines = new List<string>(Normalize(body).Split('\n'));

        // v4 旧版首行是「打开并遵守…本文件只写…差异」指针头，删掉（不再注入各家）
        var firstText = lines.FirstOrDefault(l => l.Trim().Length > 0);
        if (firstText is not null && firstText.Contains("打开并遵守", StringComparison.Ordinal))
            lines.Remove(firstText);
        else if (!legacyText.Contains("资料目录：", StringComparison.Ordinal))
            return null; // 「资料目录：」是各版正文必有的锚点，没有就不是自家旧版

        lines = lines.Where(l => !SeededMetaLines.Contains(l.Trim())).ToList();
        if (!lines.Exists(l => l.Contains("当前 `<Agent>`", StringComparison.Ordinal)))
            lines.Insert(0, "- 当前 `<Agent>` 是 `{{slug}}`");
        // 收敛删行留下的连续空行（最多留一个），避免渲染产物里出现成片空行
        var compact = new List<string>();
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0 && compact.Count > 0 && compact[^1].Trim().Length == 0) continue;
            compact.Add(line);
        }
        lines = compact;
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);

        var defaults = legacyExtras.Count > 0 ? legacyExtras
            : userTemplate is not null && IsValidPointerTemplate(userTemplate)
                ? ParsePointer(userTemplate).Extras
                : SeedExtras();

        var builder = new StringBuilder();
        builder.AppendLine(MasterManagedComment);
        if (lines.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine(string.Join("\n", lines).TrimEnd('\n'));
        }
        foreach (var slug in new[] { "cursor", "trae", "workbuddy" })
        {
            string? extra = null;
            if (legacyExtras.TryGetValue(slug, out var legacy) && legacy.Trim().Length > 0)
                extra = legacy;
            else if (defaults.TryGetValue(slug, out var fallback) && fallback.Trim().Length > 0)
                extra = fallback;
            if (extra is null) continue;
            builder.AppendLine();
            builder.AppendLine($"<!-- extra:{slug} -->");
            builder.AppendLine(extra.TrimEnd('\n'));
        }
        return builder.ToString();
    }

    /// <summary>用母本渲染某家指针：管理标记 + 母本正文全文（剔除管理注释头）+ 该家差异块（其余家的块剔除）。masterText 必须已通过 IsValidMaster。</summary>
    public static string RenderReference(string agentId, string libraryRoot, string masterText)
    {
        var (title, slug) = Identity(agentId);
        var parsed = ParsePointer(masterText);
        var body = ApplyVars(StripManagedComment(parsed.Body), title, slug, libraryRoot);
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

    public static string RenderCursor(string libraryRoot, string masterText) =>
        "---" + Environment.NewLine
        + "description: 打开并遵守共用规则" + Environment.NewLine
        + "alwaysApply: true" + Environment.NewLine
        + "---" + Environment.NewLine
        + Environment.NewLine
        + RenderReference("cursor", libraryRoot, masterText);

    public static bool LooksLikePointer(string text)
    {
        if (text.Contains(ManagedComment, StringComparison.Ordinal)) return true;
        var n = text.Replace('/', '\\');
        return n.Contains("打开并遵守", StringComparison.Ordinal)
            && (n.Contains(SharedReference, StringComparison.OrdinalIgnoreCase)
                || n.Contains("%USERPROFILE%\\.agents\\AGENTS.md", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>退役外部模板（pointer-template.md）的有效性判据：旧模板必须含「打开并遵守 + AGENTS.md 路径」。只在迁移时用来决定是否采信其差异块。</summary>
    public static bool IsValidPointerTemplate(string text)
    {
        var n = text.Replace('/', '\\');
        return n.Contains("打开并遵守", StringComparison.Ordinal)
            && (n.Contains(SharedReference, StringComparison.OrdinalIgnoreCase)
                || n.Contains("%USERPROFILE%\\.agents\\AGENTS.md", StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> MissingExtraWarnings(string masterText)
    {
        var extras = ParsePointer(masterText).Extras;
        if (extras.TryGetValue("workbuddy", out var extra) && extra.Trim().Length > 0)
            return [];
        return ["WorkBuddy 差异块缺失，记忆目录约束不会注入"];
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

    private static Dictionary<string, string> SeedExtras() => ParsePointer(ReadEmbedded("SharedRules.md")).Extras;

    /// <summary>从渲染正文里剔除母本管理注释行（含 MasterMarker 的整行），并清掉删后头部的空行。管理说明不进任何一家的规则。</summary>
    private static string StripManagedComment(string body)
    {
        var lines = new List<string>(Normalize(body).Split('\n'));
        var idx = lines.FindIndex(l => l.Contains(MasterMarker, StringComparison.Ordinal));
        if (idx < 0) return body;
        lines.RemoveAt(idx);
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        return string.Join("\n", lines);
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
        "mimocode" => ("MiMo", "MiMoCode"),
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
