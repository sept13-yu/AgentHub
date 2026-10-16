mod command;
mod model;
mod pointer;
mod store;
mod window;

use store::State;
use tauri::{AppHandle, Manager, WebviewWindow};

pub fn initialize(app: &AppHandle) -> Result<(), String> {
    store::initialize(app)
}

#[tauri::command]
pub fn desktop_quota_message(
    app: AppHandle,
    window: WebviewWindow,
    msg: String,
) -> Result<(), String> {
    command::handle(app, window, msg)
}

pub fn restore(app: &AppHandle) -> Result<(), String> {
    window::create(app).map(|_| ())
}

pub fn set_theme(app: &AppHandle, theme: &str) {
    window::set_theme(app, theme);
}

pub fn destroy(app: &AppHandle) {
    if let Some(state) = app.try_state::<State>() {
        state
            .generation
            .fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        state
            .dragging
            .store(false, std::sync::atomic::Ordering::SeqCst);
    }
    if let Some(win) = app.get_webview_window("desktop-quota") {
        window::save_position(app, &win);
        let _ = win.destroy();
    }
}

pub fn save_before_exit(app: &AppHandle) {
    if let Some(win) = app.get_webview_window("desktop-quota") {
        window::save_position(app, &win);
    } else {
        let _ = store::persist_current(app);
    }
}

pub fn toggle(app: &AppHandle) {
    let Some(win) = app.get_webview_window("desktop-quota") else {
        return;
    };
    if win.is_visible().unwrap_or(false) {
        let _ = hide(&win);
    } else if let Ok(prefs) = store::update(app, |p| p.visible = true) {
        app.state::<State>()
            .pointer_mode_dirty
            .store(true, std::sync::atomic::Ordering::SeqCst);
        let _ = win.set_ignore_cursor_events(false);
        let _ = window::resize(&win, &prefs);
        window::push_state(&win, &prefs);
        let _ = win.show();
    }
}

pub fn hide(win: &WebviewWindow) -> Result<(), String> {
    let app = win.app_handle();
    let was_visible = win.is_visible().unwrap_or(true);
    let prefs = store::update(&app, |p| p.visible = false)?;
    window::push_state(win, &prefs);
    window::save_position(&app, win);
    if let Err(e) = win.hide() {
        if let Ok(prefs) = store::update(&app, |p| p.visible = was_visible) {
            window::push_state(win, &prefs);
        }
        return Err(e.to_string());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use tauri::utils::acl::RemoteUrlPattern;

    #[test]
    fn capability_matches_ipc_origin_without_page_path() {
        let capability: serde_json::Value =
            serde_json::from_str(include_str!("../../capabilities/desktop-quota.json")).unwrap();
        let patterns: Vec<RemoteUrlPattern> = capability["remote"]["urls"]
            .as_array()
            .unwrap()
            .iter()
            .map(|url| url.as_str().unwrap().parse().unwrap())
            .collect();
        let allows = |url: &str| {
            patterns
                .iter()
                .any(|pattern| pattern.test(&url.parse().unwrap()))
        };

        // The fetch IPC transport supplies the Origin header, which has no /app/ path.
        assert!(allows("http://127.0.0.1:18780/"));
        assert!(allows("http://127.0.0.1:18780/app/#/desktop-quota"));
        assert!(!allows("http://127.0.0.1:18781/"));
        assert!(!allows("http://localhost:18780/"));
        assert!(!allows("https://127.0.0.1:18780/"));
        assert!(!allows("https://example.com/"));
    }
}
