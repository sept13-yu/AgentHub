#[cfg(not(target_os = "macos"))]
use super::model::hit_test;
#[cfg(target_os = "macos")]
use super::model::hit_test_logical;
use super::model::{outer_size, Preferences};
use super::store::{self, State};
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};
use tauri::{AppHandle, Manager, WebviewWindow};

#[cfg(target_os = "macos")]
use objc2_app_kit::NSWindow;

pub fn start_watch(app: AppHandle) {
    let state = app.state::<State>();
    let generation = state.generation.fetch_add(1, Ordering::SeqCst) + 1;
    std::thread::spawn(move || {
        let mut ignore = None;
        let mut cursor_error_logged = false;
        let mut ignore_error_logged = false;
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
            if state.pointer_mode_dirty.swap(false, Ordering::SeqCst) {
                ignore = None;
            }
            if !win.is_visible().unwrap_or(false) {
                ignore = None;
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
            if dragging && !left_mouse_down() {
                state.dragging.store(false, Ordering::SeqCst);
            }
            let desired_ignore = if state.dragging.load(Ordering::SeqCst) && left_mouse_down() {
                false
            } else {
                let (width, _) = outer_size(prefs.appearance.size as f64, prefs.expanded);
                match pointer_is_interactive(&app, &win, &prefs, width, generation) {
                    Some(inside) => !inside,
                    None => {
                        if !cursor_error_logged {
                            eprintln!("[quota] pointer position unavailable; keeping the window interactive");
                            cursor_error_logged = true;
                        }
                        false
                    }
                }
            };
            if ignore != Some(desired_ignore) {
                match win.set_ignore_cursor_events(desired_ignore) {
                    Ok(()) => {
                        ignore = Some(desired_ignore);
                        eprintln!(
                            "[quota] pointer events {}",
                            if desired_ignore {
                                "pass-through"
                            } else {
                                "interactive"
                            }
                        );
                    }
                    Err(error) => {
                        if !ignore_error_logged {
                            eprintln!("[quota] cannot change pointer event mode: {error}");
                            ignore_error_logged = true;
                        }
                    }
                }
            }
        }
    });
}

fn pointer_is_interactive(
    app: &AppHandle,
    win: &WebviewWindow,
    prefs: &Preferences,
    width: f64,
    generation: u64,
) -> Option<bool> {
    #[cfg(not(target_os = "macos"))]
    let _ = generation;

    #[cfg(target_os = "macos")]
    {
        let (x, y) = local_cursor_position(app, win, generation)?;
        Some(hit_test_logical(
            x,
            y,
            width,
            prefs.appearance.size as f64,
            prefs.expanded,
        ))
    }
    #[cfg(not(target_os = "macos"))]
    {
        let cursor = app.cursor_position().ok()?;
        let scale = win.scale_factor().ok()?;
        if !scale.is_finite() || scale <= 0.0 {
            return None;
        }
        let origin = win.inner_position().ok()?;
        Some(hit_test(
            cursor,
            origin,
            scale,
            width,
            prefs.appearance.size as f64,
            prefs.expanded,
        ))
    }
}

#[cfg(target_os = "macos")]
fn local_cursor_position(
    app: &AppHandle,
    win: &WebviewWindow,
    generation: u64,
) -> Option<(f64, f64)> {
    let app = app.clone();
    let dispatch_window = win.clone();
    let query_window = win.clone();
    let (tx, rx) = std::sync::mpsc::sync_channel(1);
    dispatch_window
        .run_on_main_thread(move || {
            let position = app
                .try_state::<State>()
                .filter(|state| state.generation.load(Ordering::SeqCst) == generation)
                .and_then(|_| {
                    let window = query_window
                        .ns_window()
                        .ok()
                        .filter(|window| !window.is_null())?;
                    let ns_window = unsafe { &*window.cast::<NSWindow>() };
                    let content_view = ns_window.contentView()?;
                    let point = ns_window.mouseLocationOutsideOfEventStream();
                    let local = content_view.convertPoint_fromView(point, None);
                    let bounds = content_view.bounds();
                    let x = local.x - bounds.origin.x;
                    let y = if content_view.isFlipped() {
                        local.y - bounds.origin.y
                    } else {
                        bounds.origin.y + bounds.size.height - local.y
                    };
                    (x.is_finite() && y.is_finite()).then_some((x, y))
                });
            let _ = tx.send(position);
        })
        .ok()?;
    rx.recv_timeout(Duration::from_millis(100)).ok().flatten()
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
