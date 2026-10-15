use super::model::{outer_size, position_is_visible, Preferences};
use super::store::{self, State};
use tauri::{
    AppHandle, Manager, Size, Theme, WebviewUrl, WebviewWindow, WebviewWindowBuilder, WindowEvent,
};

pub fn create(app: &AppHandle) -> Result<WebviewWindow, String> {
    if let Some(old) = app.get_webview_window("desktop-quota") {
        let _ = old.destroy();
    }
    let prefs = store::snapshot(app);
    let url = WebviewUrl::External(
        "http://127.0.0.1:18780/app/#/desktop-quota"
            .parse()
            .unwrap(),
    );
    let script = init_script(&prefs);
    let (width, height) = outer_size(prefs.appearance.size as f64, prefs.expanded);
    let win = WebviewWindowBuilder::new(app, "desktop-quota", url)
        .title("桌面额度")
        .transparent(true)
        .decorations(false)
        .shadow(false)
        .always_on_top(true)
        .skip_taskbar(true)
        .accept_first_mouse(true)
        .resizable(false)
        .focused(false)
        .visible(false)
        .center()
        .inner_size(width, height)
        .initialization_script(&script)
        .theme(Some(parse_theme(&prefs.theme)))
        .on_navigation(|url| {
            url.scheme() == "http"
                && url.host_str() == Some("127.0.0.1")
                && url.port() == Some(18780)
                && url.path().starts_with("/app/")
        })
        .build()
        .map_err(|e| e.to_string())?;
    if let (Some(x), Some(y)) = (prefs.x, prefs.y) {
        if let Ok(monitors) = win.available_monitors() {
            let bounds: Vec<_> = monitors
                .iter()
                .map(|m| {
                    let p = m.position();
                    let s = m.size();
                    let factor = m.scale_factor();
                    (
                        p.x as f64 / factor,
                        p.y as f64 / factor,
                        s.width as f64 / factor,
                        s.height as f64 / factor,
                    )
                })
                .collect();
            let factor = win.scale_factor().unwrap_or(1.0);
            let size = win
                .outer_size()
                .unwrap_or(tauri::PhysicalSize::new(width as u32, height as u32));
            if position_is_visible(
                x,
                y,
                size.width as f64 / factor,
                size.height as f64 / factor,
                &bounds,
            ) {
                let _ = win.set_position(tauri::LogicalPosition::new(x, y));
            }
        }
    }
    let closing = win.clone();
    win.on_window_event(move |event| {
        if let WindowEvent::CloseRequested { api, .. } = event {
            api.prevent_close();
            let _ = super::hide(&closing);
        }
    });
    if let Err(error) = win.set_ignore_cursor_events(false) {
        eprintln!("[quota] cannot enable pointer input: {error}");
    }
    if prefs.visible {
        let _ = win.show();
    }
    super::pointer::start_watch(app.clone());
    Ok(win)
}

pub fn resize(win: &WebviewWindow, prefs: &Preferences) -> Result<(), String> {
    let (width, height) = outer_size(prefs.appearance.size as f64, prefs.expanded);
    win.set_size(Size::Logical(tauri::LogicalSize::new(width, height)))
        .map_err(|e| e.to_string())
}

pub fn push_state(win: &WebviewWindow, prefs: &Preferences) {
    let state = serde_json::json!({
        "expanded": prefs.expanded,
        "windowIds": prefs.window_ids,
        "theme": prefs.theme,
        "size": prefs.appearance.size,
        "glow": prefs.appearance.glow,
        "arcMode": prefs.appearance.arc_mode,
    });
    let encoded = serde_json::to_string(&state).unwrap_or_else(|_| "{}".into());
    let script = format!("window.__AGENTHUB_DQ_STATE__={encoded};window.__AGENTHUB_THEME__={theme};window.dispatchEvent(new Event('agenthub-dq-state'));", theme = serde_json::to_string(&prefs.theme).unwrap_or_else(|_| "\"dark\"".into()));
    let _ = win.eval(&script);
}

fn init_script(prefs: &Preferences) -> String {
    let state = serde_json::json!({ "expanded": prefs.expanded, "windowIds": prefs.window_ids, "theme": prefs.theme, "size": prefs.appearance.size, "glow": prefs.appearance.glow, "arcMode": prefs.appearance.arc_mode });
    let state_json = serde_json::to_string(&state).unwrap_or_else(|_| "{}".into());
    let theme_json = serde_json::to_string(&prefs.theme).unwrap_or_else(|_| "\"dark\"".into());
    format!("window.__AGENTHUB_HOST__='tauri';window.__AGENTHUB_DESKTOP_QUOTA__=true;window.__AGENTHUB_THEME__={theme_json};window.__AGENTHUB_DQ_STATE__={state_json};")
}

pub fn set_theme(app: &AppHandle, theme: &str) {
    if let Ok(prefs) = store::update(app, |p| p.theme = theme.to_owned()) {
        if let Some(win) = app.get_webview_window("desktop-quota") {
            let _ = win.set_theme(Some(parse_theme(theme)));
            push_state(&win, &prefs);
        }
    }
}

pub fn save_position(app: &AppHandle, win: &WebviewWindow) {
    if let (Ok(position), Ok(scale)) = (win.outer_position(), win.scale_factor()) {
        if let Some(state) = app.try_state::<State>() {
            if let Ok(mut prefs) = state.prefs.lock() {
                prefs.x = Some(position.x as f64 / scale);
                prefs.y = Some(position.y as f64 / scale);
            }
        }
    }
    let _ = store::persist_current(app);
}

fn parse_theme(theme: &str) -> Theme {
    if theme == "light" {
        Theme::Light
    } else {
        Theme::Dark
    }
}
