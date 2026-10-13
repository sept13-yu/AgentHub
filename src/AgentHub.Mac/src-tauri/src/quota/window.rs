use super::model::{hit_test, position_is_visible, Preferences};
use super::store::{self, State};
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};
use tauri::{
    AppHandle, Manager, PhysicalPosition, Size, Theme, WebviewUrl, WebviewWindow,
    WebviewWindowBuilder, WindowEvent,
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
    let size = prefs.appearance.size as f64;
    let width = if prefs.expanded {
        360.0_f64.max(size)
    } else {
        size
    };
    let height = size + if prefs.expanded { 250.0 } else { 0.0 };
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
    let _ = win.set_ignore_cursor_events(true);
    if prefs.visible {
        let _ = win.show();
    }
    start_pointer_watch(app.clone());
    Ok(win)
}

pub fn resize(win: &WebviewWindow, prefs: &Preferences) -> Result<(), String> {
    let size = prefs.appearance.size as f64;
    let width = if prefs.expanded {
        360.0_f64.max(size)
    } else {
        size
    };
    let height = size + if prefs.expanded { 250.0 } else { 0.0 };
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

fn start_pointer_watch(app: AppHandle) {
    let state = app.state::<State>();
    let generation = state.generation.fetch_add(1, Ordering::SeqCst) + 1;
    std::thread::spawn(move || {
        let mut ignore = true;
        let mut dirty_at = None;
        loop {
            std::thread::sleep(Duration::from_millis(50));
            let Some(state) = app.try_state::<State>() else {
                return;
            };
            if state.generation.load(Ordering::SeqCst) != generation {
                return;
            }
            let Some(win) = app.get_webview_window("desktop-quota") else {
                return;
            };
            if !win.is_visible().unwrap_or(false) {
                continue;
            }
            let prefs = store::snapshot(&app);
            if let (Ok(position), Ok(scale)) = (win.outer_position(), win.scale_factor()) {
                let logical_x = position.x as f64 / scale;
                let logical_y = position.y as f64 / scale;
                if let Ok(mut p) = state.prefs.lock() {
                    if p.x != Some(logical_x) || p.y != Some(logical_y) {
                        p.x = Some(logical_x);
                        p.y = Some(logical_y);
                        dirty_at.get_or_insert_with(Instant::now);
                    }
                }
            }
            if dirty_at.is_some_and(|at| at.elapsed() >= Duration::from_millis(500)) {
                let _ = store::persist_current(&app);
                dirty_at = None;
            }
            let dragging = state.dragging.load(Ordering::SeqCst);
            let cursor = app.cursor_position().ok();
            if dragging && !left_mouse_down() {
                state.dragging.store(false, Ordering::SeqCst);
            }
            let desired_ignore = if state.dragging.load(Ordering::SeqCst) && left_mouse_down() {
                false
            } else {
                let scale = win.scale_factor().unwrap_or(1.0);
                let origin = win.inner_position().unwrap_or(PhysicalPosition::new(0, 0));
                let width = if prefs.expanded {
                    360.0_f64.max(prefs.appearance.size as f64)
                } else {
                    prefs.appearance.size as f64
                };
                let primary_scale = win
                    .primary_monitor()
                    .ok()
                    .flatten()
                    .map(|monitor| monitor.scale_factor())
                    .unwrap_or(scale);
                let local_scale_cursor = cursor.map(|point| {
                    PhysicalPosition::new(
                        point.x / primary_scale * scale,
                        point.y / primary_scale * scale,
                    )
                });
                local_scale_cursor.map_or(true, |point| {
                    !hit_test(
                        point,
                        origin,
                        scale,
                        width,
                        prefs.appearance.size as f64,
                        prefs.expanded,
                    )
                })
            };
            if desired_ignore != ignore {
                if win.set_ignore_cursor_events(desired_ignore).is_ok() {
                    ignore = desired_ignore;
                }
            }
        }
    });
}

#[cfg(target_os = "macos")]
fn left_mouse_down() -> bool {
    #[link(name = "CoreGraphics", kind = "framework")]
    extern "C" {
        fn CGEventSourceButtonState(state_id: i32, button: u32) -> bool;
    }
    // Combined session state (0), primary button (0). The native drag owns mouse-up.
    unsafe { CGEventSourceButtonState(0, 0) }
}

#[cfg(not(target_os = "macos"))]
fn left_mouse_down() -> bool {
    false
}
