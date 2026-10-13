use super::model::{valid_window_id, Appearance};
use super::{store, window};
use crate::AppState;
use serde::Deserialize;
use serde_json::Value;
use std::sync::atomic::Ordering;
use tauri::{AppHandle, Manager, WebviewWindow};

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct AppearanceMessage {
    size: f64,
    glow: f64,
    #[serde(rename = "arcMode")]
    arc_mode: String,
}

pub fn handle(app: AppHandle, source: WebviewWindow, msg: String) -> Result<(), String> {
    if source.label() != "desktop-quota" {
        return Err("desktop_quota_message only accepts desktop-quota".into());
    }
    if msg.len() > 2048 {
        return Err("quota message is too large".into());
    }
    let state = app
        .try_state::<AppState>()
        .ok_or_else(|| "backend is unavailable".to_string())?;
    let alive = state
        .backend
        .lock()
        .map_err(|_| "backend state unavailable")?
        .as_mut()
        .is_some_and(|backend| !backend.has_exited());
    if !alive {
        return Err("backend is unavailable".into());
    }
    let url = source.url().map_err(|e| e.to_string())?;
    if url.scheme() != "http"
        || url.host_str() != Some("127.0.0.1")
        || url.port() != Some(18780)
        || !url.path().starts_with("/app/")
    {
        return Err("quota messages only accept the local AgentHub app".into());
    }
    if msg == "dq:drag-start" {
        let quota = app.state::<super::store::State>();
        quota.dragging.store(true, Ordering::SeqCst);
        if let Err(e) = source.start_dragging() {
            quota.dragging.store(false, Ordering::SeqCst);
            return Err(e.to_string());
        }
        return Ok(());
    }
    let result = apply(&app, &source, &msg);
    if result.is_err() {
        window::push_state(&source, &store::snapshot(&app));
    }
    result
}

fn apply(app: &AppHandle, win: &WebviewWindow, msg: &str) -> Result<(), String> {
    if msg == "dq:close" {
        return super::hide(win);
    }
    let before = store::snapshot(app);
    let prefs = if msg == "dq:expanded" || msg == "dq:compact" {
        store::update(app, |p| p.expanded = msg == "dq:expanded")?
    } else if let Some(json) = msg.strip_prefix("dq:windows:") {
        let ids = parse_window_ids(json)?;
        store::update(app, |p| p.window_ids = Some(ids))?
    } else if let Some(json) = msg.strip_prefix("dq:appearance:") {
        let appearance = parse_appearance(json)?;
        store::update(app, |p| p.appearance = appearance)?
    } else {
        return Err("unsupported quota message".into());
    };
    if let Err(error) = window::resize(win, &prefs) {
        let rollback = before.clone();
        let _ = store::update(app, |p| *p = rollback);
        return Err(error);
    }
    window::push_state(win, &prefs);
    Ok(())
}

fn parse_window_ids(json: &str) -> Result<Vec<String>, String> {
    let ids: Vec<String> =
        serde_json::from_str(json).map_err(|e| format!("invalid window IDs: {e}"))?;
    if ids.len() > 2
        || ids.iter().any(|id| !valid_window_id(id))
        || ids.iter().enumerate().any(|(i, id)| ids[..i].contains(id))
    {
        return Err("window IDs must be unique, valid, and contain at most two entries".into());
    }
    Ok(ids)
}

fn parse_appearance(json: &str) -> Result<Appearance, String> {
    let raw: Value = serde_json::from_str(json).map_err(|e| format!("invalid appearance: {e}"))?;
    let object = raw
        .as_object()
        .ok_or_else(|| "appearance must be an object".to_string())?;
    if object.len() != 3 {
        return Err("appearance requires exactly size, glow, and arcMode".into());
    }
    let incoming: AppearanceMessage =
        serde_json::from_value(raw).map_err(|e| format!("invalid appearance: {e}"))?;
    Appearance::validate_message(incoming.size, incoming.glow, &incoming.arc_mode)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_only_small_unique_window_id_lists() {
        assert_eq!(
            parse_window_ids(r#"["codex:main","cursor-total"]"#).unwrap(),
            vec!["codex:main", "cursor-total"]
        );
        assert!(parse_window_ids(r#"["codex:main","codex:main"]"#).is_err());
        assert!(parse_window_ids(r#"["bad id"]"#).is_err());
        assert!(parse_window_ids(r#"["a","b","c"]"#).is_err());
    }

    #[test]
    fn appearance_message_requires_exact_valid_fields() {
        assert_eq!(
            parse_appearance(r#"{"size":360,"glow":0.12,"arcMode":"quota"}"#)
                .unwrap()
                .size,
            360
        );
        assert!(
            parse_appearance(r#"{"size":360,"glow":0.12,"arcMode":"quota","extra":1}"#).is_err()
        );
        assert!(parse_appearance(r#"{"size":300,"glow":0.7,"arcMode":"quota"}"#).is_err());
    }
}
