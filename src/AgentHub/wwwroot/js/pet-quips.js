// 移植 TokenTracker dashboard/src/lib/pet-quips.js（MIT）。
// AgentHub 桌面宠只用 zh-CN；额度条 / 多语言 / copy() 依赖留待以后。

const QUIPS = {
  empty: [
    "😴 今天还没有 tokens", "💬 发起一次对话来唤醒我！", "🌙 今天暂时很安静...",
    "⌨️ 等待你的第一个 prompt", "💤 Zzz... 还没有可统计内容", "🌅 风暴前的平静？", "✨ 我已经准备好了！",
  ],
  warmup: ["☕ 刚刚热身！", "🌱 温和开局"],
  flow: ["🎯 开始进入状态！", "💪 今天进展不错"],
  busy: ["🔥 今天很忙！", "⚡ 状态正佳！"],
  heavy: ["🚀 今天用量很高！", "🖨️ Token 机器启动"],
  massive: ["🤯 今天用量爆表！", "🔥 Token 计数器燃起来了！"],
  personality: ["👆 点我查看更多！", "📋 我来帮你计数", "✨ 每个 token 都有故事", "🤝 你的 AI 花费伙伴", "👋 你好呀~"],
};

const TODAY = { tokens: ["📊 今日：{tokens} tokens"] };

const STATS = {
  sevenDayTotal: "📅 7 天总计：{tokens} tokens",
  streakDays: "🔥 连续 {n} 天！继续保持",
  topModel: "🥇 最常用模型：{name}（{percent}）",
  runnerUp: "🥈 第二名：{name}，{percent}",
  modelCount: "🧰 使用了 {n} 个不同模型",
  conversationsToday: "💬 今日 {n} 次对话",
  busyTalker: "🗣️ {n} 次聊天，今天很忙",
};

function tierFor(tokens) {
  if (tokens <= 0) return "empty";
  if (tokens < 50_000) return "warmup";
  if (tokens < 200_000) return "flow";
  if (tokens < 500_000) return "busy";
  if (tokens < 2_000_000) return "heavy";
  return "massive";
}

function fillVars(tpl, vars) {
  return tpl.replace(/\{(\w+)\}/g, (m, k) => (k in vars ? String(vars[k]) : m));
}

export function buildQuipPool(ctx = {}, formatTokens) {
  const {
    tokens = 0, conversations = 0, last7dTokens = 0, streakDays = 0, topModels = [],
  } = ctx;
  const tokensText = formatTokens(tokens);
  const out = [];

  if (tokens <= 0) {
    out.push(...QUIPS.empty);
  } else {
    out.push(...TODAY.tokens.map((t) => fillVars(t, { tokens: tokensText })));
    out.push(...(QUIPS[tierFor(tokens)] || []));
  }

  if (last7dTokens > 0) {
    out.push(fillVars(STATS.sevenDayTotal, { tokens: formatTokens(last7dTokens) }));
  }
  if (streakDays > 1) out.push(fillVars(STATS.streakDays, { n: streakDays }));

  if (topModels.length > 0) {
    const top = topModels[0];
    const pct = typeof top.percent === "number"
      ? `${top.percent.toFixed(1)}%`
      : String(top.percent ?? "");
    out.push(fillVars(STATS.topModel, { name: top.name, percent: pct }));
    if (topModels.length >= 2) {
      const second = topModels[1];
      const pct2 = typeof second.percent === "number"
        ? `${second.percent.toFixed(1)}%`
        : String(second.percent ?? "");
      out.push(fillVars(STATS.runnerUp, { name: second.name, percent: pct2 }));
    }
    if (topModels.length >= 3) out.push(fillVars(STATS.modelCount, { n: topModels.length }));
  }

  if (conversations > 0) {
    out.push(fillVars(STATS.conversationsToday, { n: conversations }));
    if (conversations >= 10) out.push(fillVars(STATS.busyTalker, { n: conversations }));
  }

  out.push(...QUIPS.personality);
  return out;
}
