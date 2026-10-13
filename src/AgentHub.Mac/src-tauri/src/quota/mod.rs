mod command;
mod model;
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
