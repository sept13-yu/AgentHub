// 移植 TokenTracker PetAtlasAnimated.jsx（MIT）：192×208 精灵表，9 行 × 最多 8 帧。
// bot 矢量引擎与 v2 朝向表未移植。

const ROWS = {
  idle: { row: 0, durations: [280, 110, 110, 140, 140, 320] },
  "running-right": { row: 1, durations: [120, 120, 120, 120, 120, 120, 120, 220] },
  "running-left": { row: 2, durations: [120, 120, 120, 120, 120, 120, 120, 220] },
  waving: { row: 3, durations: [140, 140, 140, 280] },
  jumping: { row: 4, durations: [140, 140, 140, 140, 280] },
  failed: { row: 5, durations: [140, 140, 140, 140, 140, 140, 140, 240] },
  waiting: { row: 6, durations: [150, 150, 150, 150, 150, 260] },
  running: { row: 7, durations: [120, 120, 120, 120, 120, 220] },
  review: { row: 8, durations: [150, 150, 150, 150, 150, 280] },
};

export const ATLAS_PETS = ["sprout", "byte", "ember"];

export function isAtlasPet(id) {
  return ATLAS_PETS.includes(id);
}

export function petAtlasRowForState(state) {
  if (["error", "disconnected", "working-overheated"].includes(state)) return "failed";
  if (["happy", "waking", "mini-happy", "jumping"].includes(state)) return "jumping";
  if (["working-typing", "working-ultrathink", "working-juggling", "working-building", "working-debugger", "running"].includes(state)) {
    return "running";
  }
  if (["working-thinking", "working-wizard", "working-conducting", "working-confused", "review"].includes(state)) {
    return "review";
  }
  if (["sleeping", "idle-doze", "mini-sleep", "waiting"].includes(state)) return "waiting";
  if (["mini-peek", "waving"].includes(state)) return "waving";
  if (state === "running-left" || state === "running-right") return state;
  return "idle";
}

export function createAtlasAnimator(el) {
  let timer = 0;
  let cancelled = false;
  let lastKey = "";
  let frame = 0;
  let spec = ROWS.idle;

  function applyPosition() {
    const atlasRows = 9;
    el.style.backgroundPosition =
      `${(frame / 7) * 100}% ${(spec.row / (atlasRows - 1)) * 100}%`;
  }

  function stop() {
    cancelled = true;
    lastKey = "";
    window.clearTimeout(timer);
    el.style.backgroundImage = "";
    el.style.backgroundPosition = "";
    el.style.backgroundSize = "";
  }

  function paint(character, state) {
    const rowId = petAtlasRowForState(state);
    const key = character + ":" + rowId;
    spec = ROWS[rowId] || ROWS.idle;
    const atlasRows = 9;
    el.replaceChildren();
    el.style.backgroundImage = `url(/pets/${character}/spritesheet.webp)`;
    el.style.backgroundRepeat = "no-repeat";
    el.style.backgroundSize = `800% ${atlasRows * 100}%`;
    el.style.imageRendering = "pixelated";

    if (key === lastKey) {
      applyPosition();
      return;
    }

    cancelled = false;
    window.clearTimeout(timer);
    lastKey = key;
    frame = 0;
    applyPosition();

    if (document.visibilityState === "hidden") return;
    if (window.matchMedia?.("(prefers-reduced-motion: reduce)").matches) return;

    const advance = (current) => {
      timer = window.setTimeout(() => {
        if (cancelled) return;
        frame = (current + 1) % spec.durations.length;
        applyPosition();
        advance(frame);
      }, spec.durations[current]);
    };
    advance(0);
  }

  return { paint, stop };
}
