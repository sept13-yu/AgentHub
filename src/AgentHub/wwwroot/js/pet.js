// 移植 TokenTracker dashboard/src/pet.jsx + lib/pet-personality.js（MIT）。
// 独立透明窗入口：只渲染 clawd SVG 状态机，不进 Vue 主界面。

(() => {
  function trimNum(n, digits) {
    return n.toFixed(digits).replace(/\.?0+$/, '');
  }

  function fmtTokens(n) {
    n = Number(n) || 0;
    if (n < 0) n = 0;
    if (window.__ttPetTokenUnit !== 'en') {
      if (n >= 1e12) return trimNum(n / 1e12, 2) + '万亿';
      if (n >= 1e8) return trimNum(n / 1e8, 2) + '亿';
      if (n >= 1e7) return trimNum(n / 1e7, 2) + '千万';
      if (n >= 1e6) return trimNum(n / 1e6, 2) + '百万';
      if (n >= 1e4) return trimNum(n / 1e4, 1) + '万';
      return String(Math.round(n));
    }
    if (n >= 1e12) return trimNum(n / 1e12, 2) + 'T';
    if (n >= 1e9) return trimNum(n / 1e9, 2) + 'B';
    if (n >= 1e6) return trimNum(n / 1e6, 2) + 'M';
    if (n >= 1e3) return trimNum(n / 1e3, 1) + 'K';
    return String(Math.round(n));
  }

  const TAP_HOLD_MS = 2500;
  const DRAG_THRESHOLD = 10;
  const LEAN_DEADZONE = 0.12;
  const LEAN_MAX_TILT_DEG = 3;
  const CLAWD_FRAME_VIEWBOX = "0 0 15 16";

  const TAP_ANIMATIONS = [
    "happy", "working-wizard", "working-juggling", "working-thinking",
    "working-ultrathink", "working-typing", "disconnected", "idle-look",
    "idle-doze", "sleeping", "error",
  ];

  const PET_STATE_TO_PATH = {
    "idle-living": "idle/living.svg",
    "idle-look": "idle/look.svg",
    "idle-doze": "idle/doze.svg",
    yawning: "idle/yawn.svg",
    collapsing: "idle/collapse.svg",
    waking: "sleep/wake.svg",
    "working-typing": "working/typing.svg",
    "working-thinking": "working/thinking.svg",
    "working-ultrathink": "working/ultrathink.svg",
    "working-juggling": "working/juggling.svg",
    "working-wizard": "working/wizard.svg",
    "working-overheated": "working/overheated.svg",
    happy: "happy.svg",
    sleeping: "sleep/sleeping.svg",
    disconnected: "status/disconnected.svg",
    error: "status/error.svg",
    "static-base": "static-base.svg",
    "running-left": "mini/crabwalk.svg",
    "running-right": "mini/crabwalk.svg",
  };

  const svgCache = new Map();

  function pickPetAmbientState(stats) {
    const tokens = Number(stats.todayTokens) || 0;
    if (tokens <= 0) return "sleeping";
    const choices = ["idle-living", "idle-living", "idle-living", "idle-look"];
    if (tokens >= 200000) choices.push("working-thinking");
    if (tokens >= 500000) choices.push("working-juggling");
    if (tokens >= 2000000) choices.push("working-ultrathink");
    if ((stats.topModels && stats.topModels.length) >= 3) choices.push("working-juggling");
    if ((Number(stats.streakDays) || 0) >= 7) choices.push("working-wizard");
    const index = Math.min(choices.length - 1, Math.floor(Math.random() * choices.length));
    return choices[Math.max(0, index)];
  }

  function resolvePetState(o) {
    if (o.rage) return "working-overheated";
    if (o.connected === false) return "disconnected";
    if (o.syncing || o.typing) return "working-typing";
    if (o.celebrating) return "happy";
    if ((Number(o.todayTokens) || 0) <= 0) return "sleeping";
    return o.ambientState || "idle-living";
  }

  function readUsage() {
    const tk = Number(window.__ttPetTokens);
    return Number.isFinite(tk) ? tk : 0;
  }

  function readStats() {
    const s = window.__ttPetStats || {};
    const n = (v, f) => (Number.isFinite(Number(v)) ? Number(v) : f);
    return {
      todayTokens: n(s.todayTokens, readUsage()),
      conversations: n(s.conversations, 0),
      last7dTokens: n(s.last7dTokens, 0),
      streakDays: n(s.streakDays, 0),
      topModels: Array.isArray(s.topModels) ? s.topModels : [],
    };
  }

  function post(type) {
    try { window.chrome?.webview?.postMessage(type); } catch { /* 非壳内预览 */ }
  }

  async function fetchPetSvg(path) {
    if (svgCache.has(path)) return svgCache.get(path);
    const resp = await fetch("/clawd/" + path);
    if (!resp.ok) return null;
    const raw = await resp.text();
    const result = raw.replace(/<svg([^>]*)>/, (_m, attrs) => {
      const cleaned = String(attrs)
        .replace(/\s+width="[^"]*"/g, "")
        .replace(/\s+height="[^"]*"/g, "")
        .replace(/\s+viewBox="[^"]*"/g, "")
        .replace(/\s+preserveAspectRatio="[^"]*"/g, "");
      return `<svg${cleaned} viewBox="${CLAWD_FRAME_VIEWBOX}" preserveAspectRatio="xMidYMid meet" width="100%" height="100%">`;
    });
    svgCache.set(path, result);
    return result;
  }

  const state = {
    idleVariant: "idle-living",
    tapState: null,
    sleepState: null,
    dragState: null,
    hovering: false,
    leanX: 0,
    typing: false,
    rage: false,
    celebrating: false,
    pose: "idle-living",
  };

  const frame = document.getElementById("frame");
  const sprite = document.getElementById("sprite");
  const bubble = document.getElementById("bubble");
  const bubbleText = document.getElementById("bubble-text");
  let tapIndex = 0;
  let tapTimer = 0;
  let wakeTimer = 0;
  let celeTimer = 0;
  let dragRef = null;
  let poseToken = 0;

  function currentPose() {
    const stats = readStats();
    let auto = resolvePetState({
      rage: state.rage,
      connected: window.__ttPetConnected !== false,
      syncing: false,
      typing: state.typing,
      celebrating: state.celebrating,
      todayTokens: stats.todayTokens,
      ambientState: state.idleVariant,
    });
    const busy = auto === "working-typing" || auto === "happy"
      || auto === "working-overheated" || auto === "disconnected";
    if (state.sleepState && !busy) auto = state.sleepState;
    if (state.dragState) auto = state.dragState;
    return state.tapState || auto;
  }

  async function paint() {
    const pose = currentPose();
    state.pose = pose;
    sprite.classList.toggle("is-left", pose === "running-left");
    const tilt = state.hovering ? state.leanX * LEAN_MAX_TILT_DEG : 0;
    sprite.style.transform = tilt ? `rotate(${tilt.toFixed(2)}deg)` : "";

    const stats = readStats();
    let text = "";
    if (state.tapState) {
      const top = stats.topModels[0];
      text = top
        ? `今日 ${fmtTokens(stats.todayTokens)}\n${top.name}`
        : (stats.todayTokens > 0 ? `今日 ${fmtTokens(stats.todayTokens)}` : "今天还没消耗 token");
    } else if (state.celebrating) {
      text = "用量刚刚上去了";
    } else if (state.hovering) {
      text = stats.todayTokens > 0
        ? `今日 ${fmtTokens(stats.todayTokens)}`
        : "今天还没消耗 token";
    }
    bubbleText.textContent = text;
    bubble.classList.toggle("is-on", !!text);

    const path = PET_STATE_TO_PATH[pose] || PET_STATE_TO_PATH["static-base"];
    const token = ++poseToken;
    const html = await fetchPetSvg(path);
    if (token !== poseToken || !html) return;
    frame.innerHTML = html;
  }

  function scheduleAmbient() {
    const delay = 12000 + Math.random() * 13000;
    window.setTimeout(() => {
      state.idleVariant = pickPetAmbientState(readStats());
      paint();
      scheduleAmbient();
    }, delay);
  }

  let lastTapAt = 0;
  function triggerTap() {
    const now = Date.now();
    if (now - lastTapAt < 400) {
      lastTapAt = 0;
      post("pet:open");
      return;
    }
    lastTapAt = now;
    state.tapState = TAP_ANIMATIONS[tapIndex % TAP_ANIMATIONS.length];
    tapIndex += 1;
    clearTimeout(tapTimer);
    tapTimer = window.setTimeout(() => { state.tapState = null; paint(); }, TAP_HOLD_MS);
    paint();
  }

  window.addEventListener("pet:usage", paint);
  window.addEventListener("pet:mode", paint);
  window.addEventListener("pet:connected", paint);
  window.addEventListener("pet:hover", () => {
    state.hovering = Boolean(window.__ttPetHover);
    if (!state.hovering) state.leanX = 0;
    paint();
  });
  window.addEventListener("pet:typing", () => { state.typing = Boolean(window.__ttPetTyping); paint(); });
  window.addEventListener("pet:rage", () => { state.rage = Boolean(window.__ttPetRage); paint(); });
  window.addEventListener("pet:drag-state", () => {
    const next = window.__ttPetDragState;
    state.dragState = next === "running-left" || next === "running-right" ? next : null;
    paint();
  });
  window.addEventListener("pet:drag-end", () => { state.dragState = null; paint(); });
  window.addEventListener("pet:wake", () => {
    clearTimeout(wakeTimer);
    state.sleepState = "waking";
    wakeTimer = window.setTimeout(() => { state.sleepState = null; paint(); }, 1500);
    paint();
  });
  window.addEventListener("pet:sleep", (e) => {
    clearTimeout(wakeTimer);
    state.sleepState = e.detail?.phase || "sleeping";
    paint();
  });
  window.addEventListener("pet:model-status", () => {
    clearTimeout(celeTimer);
    state.celebrating = true;
    celeTimer = window.setTimeout(() => { state.celebrating = false; paint(); }, 3000);
    paint();
  });

  document.addEventListener("mousedown", (e) => {
    if (e.button !== 0) return;
    dragRef = { x: e.clientX, y: e.clientY, dragging: false };
  });
  document.addEventListener("mousemove", (e) => {
    const d = dragRef;
    if (!d || !d.dragging) {
      let f = (e.clientX / window.innerWidth - 0.5) * 2;
      if (Math.abs(f) < LEAN_DEADZONE) f = 0;
      const next = Math.max(-1, Math.min(1, f));
      if (next !== state.leanX) { state.leanX = next; paint(); }
    }
    if (!d || d.dragging) return;
    if (Math.abs(e.clientX - d.x) > DRAG_THRESHOLD || Math.abs(e.clientY - d.y) > DRAG_THRESHOLD) {
      const deltaX = e.clientX - d.x;
      d.dragging = true;
      dragRef = null;
      post(deltaX < 0 ? "pet:drag-left" : "pet:drag-right");
    }
  });
  document.addEventListener("mouseup", () => {
    const d = dragRef;
    dragRef = null;
    if (d && !d.dragging) triggerTap();
  });
  document.addEventListener("contextmenu", (e) => {
    e.preventDefault();
    post("pet:context-menu");
  });

  scheduleAmbient();
  paint();
})();
